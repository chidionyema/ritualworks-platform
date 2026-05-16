# Media Service — World-Class Uplift Specification

> Generated 2026-05-16. Covers: S3 multipart uploads (256GB), S3 event notifications,
> video transcoding (HLS/DASH), image optimization, audio normalization, upload-complete
> notifications, and all edge cases/gaps found in current implementation.
>
> Every item: exact file:line, root cause, fix with code, test spec.
> Severity: CRITICAL > HIGH > MEDIUM > LOW

---

## Current State

The Media service (`src/Media/Media.Api/`) is a minimal upload + virus-scan pipeline:

- **Domain**: `MediaFile` entity with `Pending → Quarantined → Active/Rejected` state machine
- **Upload**: Client-initiated presigned PUT URL (single-part only)
- **Scan**: ClamAV via nClam, 25MB `MaxStreamSize` hard limit
- **Storage**: `IS3Service` with `GeneratePreSignedUrl` + `DownloadAsync`
- **No events**: No `src/Contracts/Media/` — service is fire-and-forget
- **No processing**: No transcoding, no thumbnails, no optimization
- **No notifications**: No upload-complete event, no user notification
- **Broken tests**: Unit tests have wrong constructor signatures, won't compile

The Content service (`src/Content/`) already has multipart upload orchestration, presigned
part URLs, an upload sweeper, and ImageSharp referenced (but unused). We should leverage
those patterns where appropriate but keep Media as the dedicated media-processing service.

---

## Architecture Overview

```
Client                    Media API                S3 (Tigris/AWS)
  │                          │                          │
  ├─ POST /initiate ────────►│                          │
  │◄── presigned URLs ───────┤                          │
  │                          │                          │
  ├─ PUT part 1 ─────────────┼─────────────────────────►│
  ├─ PUT part 2 ─────────────┼─────────────────────────►│
  ├─ ...                     │                          │
  │                          │                          │
  │                          │◄── S3 Event (SQS) ──────┤  ← NEW: server-side notification
  │                          │                          │
  │                          ├─ Virus scan ────────────►│  (download + ClamAV)
  │                          ├─ Transcode/optimize ────►│  (FFmpeg/ImageSharp)
  │                          ├─ Generate thumbnails ───►│  (to S3)
  │                          │                          │
  │◄── Notification ─────────┤  (via MassTransit → Notifications service)
  │    (email/push/realtime) │
```

**Key design decisions**:
1. **S3 event notifications** replace client-side `POST /complete` — server knows when upload finishes
2. **Multipart for large files** (> 8MB threshold, up to 256GB via 10,000 × 25MB parts)
3. **Processing pipeline** is event-driven: S3 upload → scan → process → notify
4. **Media contracts** published via outbox for cross-service consumption
5. **Fail-closed security**: any scan/processing failure → Rejected, never served

---

## CRITICAL — Existing Bugs

### BUG-01: Migration creates wrong unique index (global Hash, not composite)

- **File**: `src/Media/Media.Api/Migrations/20260516021206_InitialCreate.cs:36-41`
- **Root cause**: Migration creates `IX_MediaFiles_Hash` as unique on `Hash` alone. But `MediaDbContext.OnModelCreating` specifies `HasIndex(e => new { e.Hash, e.OwnerId }).IsUnique()`. Two users uploading the same file (same SHA-256) collide.
- **Fix**: New migration to drop the incorrect index and create the composite one:
  ```csharp
  migrationBuilder.DropIndex("IX_MediaFiles_Hash", schema: "media", table: "MediaFiles");
  migrationBuilder.CreateIndex(
      name: "IX_MediaFiles_Hash_OwnerId",
      schema: "media", table: "MediaFiles",
      columns: new[] { "Hash", "OwnerId" },
      unique: true);
  ```
- **Test spec**: `Two_owners_can_upload_same_hash` — seed file for owner A, upload same hash as owner B, verify both succeed

### BUG-02: Unit tests have wrong constructor signatures (won't compile)

- **File**: `tests/Media/Media.Unit/ProcessVirusScanTests.cs:21` — `new ProcessVirusScanHandler(_context)` missing 3 deps
- **File**: `tests/Media/Media.Unit/InitiateUploadTests.cs:24` — missing `ICurrentUserService` mock
- **Fix**: Fix constructors to match actual handler signatures. Add mocks for `IVirusScanner`, `ICurrentUserService`, `IS3Service`.
- **Test spec**: All existing unit tests must compile and pass

### BUG-03: ClamAV MaxStreamSize = 25MB blocks legitimate files

- **File**: `src/Media/Media.Api/Infrastructure/ClamAvScanner.cs:52`
- **Root cause**: `MaxStreamSize = 25_214_400` (25MB). Files larger than 25MB cannot be scanned. With 256GB upload support, this is a showstopper.
- **Fix**: Stream file in chunks to ClamAV via `INSTREAM` protocol (nClam supports this natively via `SendAndScanFileAsync` with streams). For files > configured threshold (e.g., 100MB), use `clamd`'s `SCAN` command against the local filesystem instead — download to temp file, scan on-disk, delete.
  ```csharp
  if (fileSize > _opts.InStreamMaxBytes)
  {
      // Download to temp, scan via SCAN command (no 25MB limit)
      var tempPath = Path.Combine(Path.GetTempPath(), $"media-scan-{Guid.NewGuid()}");
      try
      {
          await using var fs = File.Create(tempPath);
          await s3Stream.CopyToAsync(fs, ct);
          var result = await clam.ScanFileOnServerAsync(tempPath, ct);
          return result.Result == ClamScanResults.Clean;
      }
      finally { File.Delete(tempPath); }
  }
  ```
- **Test spec**: `Large_file_scanned_via_filesystem_mode` — mock 100MB+ stream, verify scan completes

### BUG-04: No concurrency token on MediaFile (lost updates)

- **File**: `src/Media/Media.Api/Infrastructure/MediaDbContext.cs`
- **Root cause**: No `xmin` concurrency token. Concurrent status transitions (e.g., scan completing while admin quarantines) silently overwrite.
- **Fix**: Add to OnModelCreating:
  ```csharp
  entity.Property<uint>("xmin")
      .HasColumnType("xid")
      .ValueGeneratedOnAddOrUpdate()
      .IsConcurrencyToken();
  ```
- **Test spec**: `Concurrent_status_update_throws_DbUpdateConcurrencyException`

### BUG-05: State machine allows invalid transitions

- **File**: `src/Media/Media.Api/Domain/MediaFile.cs`
- **Root cause**: `MarkAsQuarantined()` has no guard — can regress Active back to Quarantined. `MarkAsActive()` has no guard — can promote Rejected to Active.
- **Fix**: Add transition guards:
  ```csharp
  public void MarkAsQuarantined()
  {
      if (Status != MediaStatus.Pending)
          throw new InvalidOperationException($"Cannot quarantine from {Status}");
      Status = MediaStatus.Quarantined;
  }
  public void MarkAsActive()
  {
      if (Status != MediaStatus.Quarantined)
          throw new InvalidOperationException($"Cannot activate from {Status}");
      Status = MediaStatus.Active;
  }
  public void MarkAsRejected()
  {
      if (Status != MediaStatus.Quarantined)
          throw new InvalidOperationException($"Cannot reject from {Status}");
      Status = MediaStatus.Rejected;
  }
  ```
- **Test spec**: `MarkAsActive_from_Pending_throws`, `MarkAsQuarantined_from_Active_throws`

---

## CRITICAL — New Capabilities

### C-01: S3 Event Notifications (upload-complete detection)

- **Why**: Currently the client must call `POST /complete` after upload. This is fragile — if the client crashes, the file stays in Pending forever. S3 event notifications guarantee the server learns about completed uploads.
- **Design**:
  1. Configure S3 bucket notification → SQS queue (or SNS → SQS) for `s3:ObjectCreated:*`
  2. New `SqsS3EventConsumer` background worker polls SQS
  3. On receipt: extract bucket/key, look up `MediaFile` by S3 key, trigger virus scan pipeline
  4. Idempotent: if file already scanned (not Pending), skip
  5. Dead-letter queue for poison messages after 3 retries
- **Files to create**:
  - `src/Media/Media.Api/Infrastructure/S3EventConsumer.cs` — SQS polling worker
  - `src/Media/Media.Api/Options/S3NotificationOptions.cs` — SQS URL, region, polling interval
- **Config**:
  ```json
  {
    "S3Notifications": {
      "Enabled": true,
      "SqsQueueUrl": "https://sqs.us-east-1.amazonaws.com/123/media-upload-events",
      "Region": "us-east-1",
      "PollIntervalSeconds": 5,
      "MaxMessages": 10,
      "VisibilityTimeoutSeconds": 300
    }
  }
  ```
- **Edge cases**:
  - S3 may send duplicate events → idempotent handler (check MediaFile.Status)
  - S3 may send event for non-existent MediaFile (direct S3 upload without API) → log + skip
  - SQS visibility timeout must exceed max scan + transcode time
  - LocalStack SQS for dev/test parity
- **Fallback**: Keep `POST /complete` endpoint as belt-and-braces for clients that need synchronous confirmation. S3 event is the primary path.
- **Test spec**:
  - `S3Event_triggers_virus_scan_for_pending_file`
  - `S3Event_skips_already_scanned_file`
  - `S3Event_skips_unknown_key`
  - `Duplicate_S3Event_is_idempotent`

### C-02: Multipart Upload Support (256GB)

- **Why**: Current single presigned PUT has a 5GB S3 limit. For 256GB video files, multipart is mandatory.
- **Design**: Follow Content service's proven pattern:
  1. `POST /api/media/initiate` — if `Size > SinglePutMaxBytes` (8MB default), initiate S3 multipart upload, return upload ID + part count + presigned URLs for each part
  2. Client uploads parts directly to S3 in parallel
  3. `POST /api/media/{id}/complete-multipart` — receives list of `{ PartNumber, ETag }`, calls S3 `CompleteMultipartUploadAsync`
  4. S3 event notification fires → triggers scan pipeline
  5. `POST /api/media/{id}/abort` — cancels in-flight multipart upload, cleans up S3 parts
- **Domain changes**:
  - Add to `MediaFile`: `S3UploadId` (nullable string), `UploadKind` enum (Single/Multipart), `PartCount` (int)
  - Add `MediaFile.InitiateMultipart(uploadId, partCount)`
- **IS3Service extensions**:
  ```csharp
  Task<string> InitiateMultipartUploadAsync(string key, string mimeType, CancellationToken ct);
  string GeneratePartPresignedUrl(string key, string uploadId, int partNumber);
  Task CompleteMultipartUploadAsync(string key, string uploadId, IList<PartETag> parts, CancellationToken ct);
  Task AbortMultipartUploadAsync(string key, string uploadId, CancellationToken ct);
  ```
- **Edge cases**:
  - Part size: 5MB min (S3 requirement), 25MB recommended, last part can be smaller
  - Max 10,000 parts → 25MB × 10,000 = 250GB. For 256GB: use 26MB parts.
  - Part presigned URLs expire → client must request fresh URLs for retried parts
  - Orphaned multipart uploads → `UploadSweeperWorker` aborts uploads older than `PendingUploadTtl` (default 6h)
  - Network failure mid-upload → client retries individual parts (S3 is idempotent per part)
  - S3 lifecycle rule as safety net: abort incomplete multiparts after 7 days
- **Files to create**:
  - `src/Media/Media.Api/Application/InitiateMultipartUpload.cs`
  - `src/Media/Media.Api/Application/CompleteMultipartUpload.cs`
  - `src/Media/Media.Api/Application/AbortUpload.cs`
  - `src/Media/Media.Api/Application/RefreshPartUrls.cs` — regenerate expired part URLs
  - `src/Media/Media.Api/Infrastructure/Workers/UploadSweeperWorker.cs`
  - `src/Media/Media.Api/Options/UploadOptions.cs`
- **Config**:
  ```json
  {
    "Upload": {
      "SinglePutMaxBytes": 8388608,
      "PartSizeBytes": 26214400,
      "MaxFileSizeBytes": 274877906944,
      "PendingUploadTtlHours": 6,
      "SweeperIntervalMinutes": 5,
      "PresignedUrlExpiryMinutes": 60
    }
  }
  ```
- **Test spec**:
  - `Initiate_large_file_returns_multipart_upload_with_part_urls`
  - `Complete_multipart_with_valid_etags_activates_scan_pipeline`
  - `Abort_multipart_cleans_up_s3_and_marks_failed`
  - `Sweeper_aborts_stale_multipart_uploads`
  - `Refresh_part_urls_returns_new_presigned_urls`

### C-03: Media Contracts & Events

- **Why**: No `src/Contracts/Media/` exists. Other services can't react to media lifecycle events. Notifications service needs events to notify users.
- **Events to create** in `src/Contracts/Media/MediaEvents.cs`:
  ```csharp
  namespace Haworks.Contracts.Media;

  /// Fired when upload initiation succeeds (presigned URLs generated)
  public sealed record MediaUploadInitiatedEvent : DomainEvent
  {
      public required Guid MediaId { get; init; }
      public required string OwnerId { get; init; }
      public required string FileName { get; init; }
      public required string MimeType { get; init; }
      public required long Size { get; init; }
  }

  /// Fired when S3 confirms upload is complete (all bytes received)
  public sealed record MediaUploadCompletedEvent : DomainEvent
  {
      public required Guid MediaId { get; init; }
      public required string OwnerId { get; init; }
      public required string FileName { get; init; }
      public required string MimeType { get; init; }
      public required long Size { get; init; }
  }

  /// Fired when virus scan passes and file is marked Active
  public sealed record MediaScanPassedEvent : DomainEvent
  {
      public required Guid MediaId { get; init; }
      public required string OwnerId { get; init; }
      public required string FileName { get; init; }
      public required string MimeType { get; init; }
  }

  /// Fired when virus scan fails and file is quarantined/rejected
  public sealed record MediaScanFailedEvent : DomainEvent
  {
      public required Guid MediaId { get; init; }
      public required string OwnerId { get; init; }
      public required string FileName { get; init; }
      public required string Reason { get; init; }
  }

  /// Fired when media processing (transcode/thumbnails) completes
  public sealed record MediaProcessingCompletedEvent : DomainEvent
  {
      public required Guid MediaId { get; init; }
      public required string OwnerId { get; init; }
      public required string FileName { get; init; }
      public required IReadOnlyList<MediaVariant> Variants { get; init; }
  }

  /// Fired when media processing fails
  public sealed record MediaProcessingFailedEvent : DomainEvent
  {
      public required Guid MediaId { get; init; }
      public required string OwnerId { get; init; }
      public required string Reason { get; init; }
  }

  /// Represents a processed variant (thumbnail, HLS playlist, WebP conversion, etc.)
  public sealed record MediaVariant
  {
      public required string Kind { get; init; }       // "thumbnail", "hls", "webp", "audio-normalized"
      public required string S3Key { get; init; }
      public required string MimeType { get; init; }
      public required long Size { get; init; }
      public int? Width { get; init; }
      public int? Height { get; init; }
      public int? DurationMs { get; init; }
  }
  ```
- **Test spec**: Architecture guard — `Media_events_extend_DomainEvent`

### C-04: Upload-Complete Notifications to Users

- **Why**: User explicitly requested "notification for when uploads complete"
- **Design**: Follow the established Notifications pattern (RefundEmailConsumer style):
  1. Media service publishes `MediaProcessingCompletedEvent` or `MediaScanPassedEvent` via outbox
  2. Notifications service has `MediaUploadEmailConsumer`:
     ```csharp
     public sealed class MediaUploadEmailConsumer(IMediator mediator) :
         IConsumer<MediaProcessingCompletedEvent>,
         IConsumer<MediaScanFailedEvent>
     {
         public async Task Consume(ConsumeContext<MediaProcessingCompletedEvent> ctx)
         {
             var msg = ctx.Message;
             await mediator.Send(new SendNotificationCommand(
                 UserId: msg.OwnerId,
                 Recipient: null,  // resolved from UserId by preference service
                 Channel: NotificationChannel.Push,  // real-time first
                 TemplateId: "media-upload-complete",
                 Priority: NotificationPriority.Normal,
                 Variables: new Dictionary<string, object>
                 {
                     ["FileName"] = msg.FileName,
                     ["VariantCount"] = msg.Variants.Count,
                     ["MediaId"] = msg.MediaId,
                 },
                 IdempotencyKey: $"media-complete-{msg.MediaId}"));
         }
     }
     ```
  3. Realtime service also consumes `MediaProcessingCompletedEvent` for SignalR push:
     ```csharp
     await Clients.User(msg.OwnerId).SendAsync("MediaReady", new { msg.MediaId, msg.FileName });
     ```
- **Templates to create**:
  - `media-upload-complete` — "Your file {{FileName}} has been processed and is ready"
  - `media-upload-failed` — "Your file {{FileName}} could not be processed: {{Reason}}"
- **Edge cases**:
  - User offline → SignalR message stored in Redis inbox, delivered on reconnect
  - User has push notifications disabled → email fallback via preference service
  - Duplicate events → idempotency key `media-complete-{MediaId}` prevents double notification
- **Test spec**:
  - `MediaProcessingCompleted_sends_push_notification`
  - `MediaScanFailed_sends_failure_notification`

---

## HIGH — Processing Pipeline

### H-01: Video Transcoding (HLS/DASH via FFmpeg)

- **Why**: Raw video uploads (MOV, AVI, MKV) need adaptive bitrate streaming for bandwidth-constrained clients.
- **Design**:
  1. After virus scan passes, if `MimeType.StartsWith("video/")`:
  2. `VideoTranscodeWorker` (BackgroundService) consumes `MediaScanPassedEvent`
  3. Downloads original from S3 to temp directory
  4. Runs FFmpeg via `System.Diagnostics.Process`:
     ```
     ffmpeg -i input.mp4 \
       -map 0:v -map 0:a \
       -c:v libx264 -preset medium -crf 23 \
       -c:a aac -b:a 128k \
       -hls_time 6 -hls_list_size 0 \
       -hls_segment_filename 'segment_%03d.ts' \
       -f hls playlist.m3u8
     ```
  5. Generates multiple quality tiers:
     - **1080p** (5000 kbps) — if source ≥ 1080p
     - **720p** (2500 kbps) — if source ≥ 720p
     - **480p** (1000 kbps) — always
     - **360p** (500 kbps) — always (mobile fallback)
  6. Uploads all segments + master playlist to S3 under `media/{id}/hls/`
  7. Publishes `MediaProcessingCompletedEvent` with HLS variants
  8. Cleans up temp files
- **Files to create**:
  - `src/Media/Media.Api/Infrastructure/Processing/VideoTranscodeWorker.cs`
  - `src/Media/Media.Api/Infrastructure/Processing/FfmpegService.cs`
  - `src/Media/Media.Api/Options/TranscodeOptions.cs`
- **Config**:
  ```json
  {
    "Transcode": {
      "Enabled": true,
      "FfmpegPath": "/usr/bin/ffmpeg",
      "FfprobePath": "/usr/bin/ffprobe",
      "TempDirectory": "/tmp/media-transcode",
      "MaxConcurrentJobs": 2,
      "TimeoutMinutes": 120,
      "HlsSegmentSeconds": 6,
      "QualityTiers": [
        { "Name": "1080p", "Height": 1080, "VideoBitrateKbps": 5000, "MinSourceHeight": 1080 },
        { "Name": "720p",  "Height": 720,  "VideoBitrateKbps": 2500, "MinSourceHeight": 720 },
        { "Name": "480p",  "Height": 480,  "VideoBitrateKbps": 1000, "MinSourceHeight": 0 },
        { "Name": "360p",  "Height": 360,  "VideoBitrateKbps": 500,  "MinSourceHeight": 0 }
      ]
    }
  }
  ```
- **Edge cases**:
  - FFmpeg crash/timeout → mark MediaFile as `ProcessingFailed`, publish `MediaProcessingFailedEvent`
  - Corrupt video file → FFprobe returns non-zero exit code → reject early
  - Disk space exhaustion → check available space before starting, fail gracefully
  - Audio-only video → skip video transcode, extract audio only
  - Very long video (8+ hours) → enforce `MaxDurationMinutes` config, reject if exceeded
  - FFmpeg not installed → `Enabled: false` skips transcode, file served as-is
  - Temp directory cleanup on crash → sweeper deletes orphaned temp files older than 4h
- **Test spec**:
  - `Video_upload_triggers_hls_transcode`
  - `Corrupt_video_rejected_before_transcode`
  - `Transcode_timeout_marks_file_as_failed`
  - `Transcode_disabled_skips_processing`

### H-02: Image Optimization (Thumbnails, WebP/AVIF, Responsive)

- **Why**: Raw image uploads (PNG, JPEG, TIFF) need thumbnails for listings, WebP/AVIF for bandwidth savings, and responsive srcset for different screen sizes.
- **Design**:
  1. After virus scan passes, if `MimeType.StartsWith("image/")`:
  2. `ImageProcessingWorker` consumes `MediaScanPassedEvent`
  3. Uses **SixLabors.ImageSharp** (already in Content.Infrastructure.csproj):
     - Generate thumbnails: 150×150, 300×300, 600×600 (fit within, maintain aspect ratio)
     - Convert to WebP (lossy, quality 80) and AVIF (quality 50) for each size
     - Extract EXIF metadata (dimensions, camera, GPS) — strip GPS before storing for privacy
     - Generate blurhash placeholder (for progressive loading UX)
  4. Uploads variants to S3 under `media/{id}/img/`
  5. Publishes `MediaProcessingCompletedEvent` with image variants
- **Files to create**:
  - `src/Media/Media.Api/Infrastructure/Processing/ImageProcessingWorker.cs`
  - `src/Media/Media.Api/Infrastructure/Processing/ImageOptimizer.cs`
  - `src/Media/Media.Api/Options/ImageOptions.cs`
- **Config**:
  ```json
  {
    "Image": {
      "Enabled": true,
      "ThumbnailSizes": [150, 300, 600],
      "WebPQuality": 80,
      "AvifQuality": 50,
      "MaxDimensionPixels": 16384,
      "StripExifGps": true,
      "GenerateBlurhash": true
    }
  }
  ```
- **Edge cases**:
  - Image too large to load into memory (e.g., 500MP panorama) → stream-based resize, enforce `MaxDimensionPixels`
  - Animated GIF/WebP → preserve animation in thumbnails (or generate static thumbnail + keep original)
  - CMYK color space → convert to sRGB before WebP/AVIF (they only support sRGB)
  - Transparent PNG → WebP preserves alpha, AVIF preserves alpha, JPEG thumbnail uses white background
  - Corrupt image → ImageSharp throws → mark as ProcessingFailed
  - SVG uploads → skip raster processing (serve as-is), but scan for embedded scripts (XSS vector)
  - EXIF rotation → apply rotation before thumbnailing (ImageSharp `AutoOrient()`)
- **Test spec**:
  - `Image_upload_generates_3_thumbnail_sizes`
  - `Image_generates_webp_and_avif_variants`
  - `Exif_gps_stripped_from_processed_images`
  - `Animated_gif_preserved_in_thumbnails`
  - `Corrupt_image_fails_gracefully`

### H-03: Audio Normalization

- **Why**: User-uploaded audio files have wildly different volume levels. Normalization ensures consistent playback.
- **Design**:
  1. After virus scan passes, if `MimeType.StartsWith("audio/")`:
  2. `AudioProcessingWorker` consumes `MediaScanPassedEvent`
  3. Uses FFmpeg for normalization:
     ```
     ffmpeg -i input.mp3 -af loudnorm=I=-16:LRA=11:TP=-1.5 -c:a aac -b:a 128k output.m4a
     ```
  4. Also generates waveform data (JSON array of amplitude samples) for UI visualization
  5. Uploads normalized audio + waveform to S3 under `media/{id}/audio/`
  6. Publishes `MediaProcessingCompletedEvent` with audio variants
- **Files to create**:
  - `src/Media/Media.Api/Infrastructure/Processing/AudioProcessingWorker.cs`
- **Edge cases**:
  - Silence-only audio → normalization produces near-zero output, skip normalization
  - Very long audio (podcast, 4+ hours) → enforce max duration, process anyway but with lower priority
  - DRM-protected audio → FFmpeg fails → mark as ProcessingFailed with clear error
- **Test spec**:
  - `Audio_upload_triggers_normalization`
  - `Audio_generates_waveform_data`

---

## HIGH — Infrastructure

### H-04: MassTransit + Outbox Integration

- **Why**: Media service currently has no MassTransit. Events are published nowhere. Processing results are invisible to the platform.
- **Design**: Follow Payments/Privacy pattern:
  ```csharp
  // In Media Program.cs or DependencyInjection
  services.AddMassTransit(mt =>
  {
      mt.SetKebabCaseEndpointNameFormatter();
      mt.AddDelayedMessageScheduler();
      mt.AddConsumer<GlobalFaultConsumer>();

      mt.AddConsumer<MediaScanPassedConsumer>();  // triggers processing pipeline

      mt.AddEntityFrameworkOutbox<MediaDbContext>(o =>
      {
          o.UsePostgres();
          o.UseBusOutbox();
          o.QueryDelay = TimeSpan.FromMilliseconds(100);
          o.DuplicateDetectionWindow = TimeSpan.FromMinutes(30);
      });

      mt.UsingRabbitMq((context, cfg) =>
      {
          cfg.Host(new Uri(rabbitConn));
          cfg.UseDelayedMessageScheduler();
          cfg.ConfigureStandardRabbitMq(context);
      });
  });
  ```
- **Migration**: Add inbox/outbox tables to MediaDbContext
- **Test spec**: `MediaUploadCompleted_event_published_via_outbox`

### H-05: Processing State Machine (Domain)

- **Why**: Current `MediaStatus` enum is too simple for a processing pipeline. Need intermediate states.
- **New enum**:
  ```csharp
  public enum MediaStatus
  {
      Pending,           // Upload initiated, waiting for S3 bytes
      Uploaded,          // S3 confirms upload complete
      Scanning,          // ClamAV scan in progress (was: Quarantined)
      ScanFailed,        // Virus detected or scan error (was: Rejected)
      Processing,        // Transcode/optimize in progress
      ProcessingFailed,  // Processing error (file still clean, just not optimized)
      Active,            // Fully processed and available
      Deleted            // Soft-deleted
  }
  ```
- **Valid transitions**:
  ```
  Pending → Uploaded → Scanning → Active (no processing needed)
                                → Processing → Active
                                → Processing → ProcessingFailed
                                → ScanFailed
  Pending → Deleted (abort)
  Active → Deleted (soft-delete)
  ProcessingFailed → Processing (retry)
  ```
- **Edge case**: `ProcessingFailed` files are still clean — they should be serveable as original (without transcoded variants). Only `ScanFailed` files are truly blocked.
- **Test spec**: All invalid transitions throw `InvalidOperationException`

### H-06: GET endpoint exposes OwnerId to non-owners

- **File**: `src/Media/Media.Api/Controllers/MediaController.cs` — GET /api/media/{id}
- **Root cause**: Returns full `MediaFileResponse` including `OwnerId` to any authenticated user
- **Fix**: Only include `OwnerId` if caller is the owner. Or remove `OwnerId` from the public DTO entirely — the owner knows their own ID.
  ```csharp
  public sealed record MediaFileResponse(
      Guid Id, string FileName, string MimeType, long Size,
      string Status, DateTime CreatedAt,
      IReadOnlyList<MediaVariantResponse>? Variants);
  ```
- **Test spec**: `GET_media_does_not_expose_ownerId`

---

## MEDIUM — Operational

### M-01: Upload sweeper for orphaned multipart uploads

- **Design**: `UploadSweeperWorker` (BackgroundService) runs every 5 minutes:
  1. Query MediaFiles where `Status == Pending` and `CreatedAt < now - PendingUploadTtl`
  2. For multipart: call `AbortMultipartUploadAsync`
  3. Mark as `Deleted`
  4. Log metrics
- **Edge case**: Race with legitimate slow upload → use generous TTL (6 hours default)
- **Test spec**: `Sweeper_aborts_stale_uploads_and_marks_deleted`

### M-02: Processing retry with backoff

- **Design**: If processing fails (FFmpeg crash, ImageSharp OOM), allow manual retry:
  - `POST /api/media/{id}/retry-processing` — re-publishes `MediaScanPassedEvent`
  - Max 3 retries, tracked on entity (`ProcessingAttempts` int)
  - Exponential backoff: 1min, 5min, 30min
- **Edge case**: Same processing bug → fails 3 times → permanent `ProcessingFailed`, alert ops
- **Test spec**: `Retry_processing_republishes_scan_passed_event`

### M-03: Presigned GET URLs for serving media

- **Why**: No download endpoint exists. Clients need presigned GET URLs to fetch files and variants.
- **Design**:
  - `GET /api/media/{id}/url` — returns presigned GET URL for original (owner only)
  - `GET /api/media/{id}/url?variant=thumbnail-300` — returns presigned GET URL for specific variant
  - Presigned URLs expire after configurable TTL (default 1 hour)
  - Only Active/ProcessingFailed files (ProcessingFailed = serve original)
  - ScanFailed files → 403 Forbidden
- **Test spec**:
  - `Get_url_returns_presigned_get_for_active_file`
  - `Get_url_for_scan_failed_returns_403`
  - `Get_url_for_variant_returns_correct_s3_key`

### M-04: File type allowlist

- **Why**: No MIME type validation beyond `MaxLength(100)`. Users could upload executables (.exe, .dll).
- **Design**: Allowlist of permitted MIME types:
  ```json
  {
    "Upload": {
      "AllowedMimeTypes": [
        "image/jpeg", "image/png", "image/gif", "image/webp", "image/avif", "image/svg+xml",
        "video/mp4", "video/quicktime", "video/x-msvideo", "video/x-matroska", "video/webm",
        "audio/mpeg", "audio/mp4", "audio/ogg", "audio/wav", "audio/flac", "audio/webm",
        "application/pdf"
      ]
    }
  }
  ```
- **Validation**: Check MIME type in `InitiateUploadValidator`. Also validate file signature (magic bytes) during virus scan phase — MIME type from client is untrusted.
- **Edge case**: `application/octet-stream` uploads → reject (force correct MIME type)
- **Test spec**: `Upload_with_disallowed_mime_type_rejected`

### M-05: Rate limiting on upload initiation

- **Why**: No rate limit on `POST /initiate`. Attacker could initiate millions of uploads, exhausting S3 presigned URL generation and DB rows.
- **Design**: Per-user rate limit: 100 uploads/hour, 1000/day. Use `System.Threading.RateLimiting` middleware.
- **Test spec**: `Upload_rate_limit_rejects_101st_request`

### M-06: MediaFile list/query endpoint with pagination

- **Why**: No list endpoint exists. Users can't browse their uploads.
- **Design**:
  - `GET /api/media?page=1&pageSize=20&status=Active` — paginated list, filtered by owner from JWT
  - Max pageSize clamped to 100
  - Filter by status, MIME type prefix (image/*, video/*, audio/*)
  - Sort by CreatedAt descending (newest first)
- **Test spec**: `List_media_returns_only_callers_files`

---

## LOW — Polish

### L-01: Health check for ClamAV connectivity

- **Design**: `ClamAvHealthCheck : IHealthCheck` — pings ClamAV daemon, returns Healthy/Unhealthy
- **Test spec**: `ClamAv_health_check_reports_unhealthy_when_down`

### L-02: Health check for S3 connectivity

- **Design**: `S3HealthCheck : IHealthCheck` — calls `HeadBucket`, returns Healthy/Unhealthy
- **Test spec**: `S3_health_check_reports_healthy_when_bucket_exists`

### L-03: OpenTelemetry spans for processing pipeline

- **Design**: Add custom spans for:
  - `media.upload.initiate`
  - `media.scan.start` / `media.scan.complete`
  - `media.transcode.start` / `media.transcode.complete`
  - `media.image.optimize`
  - `media.audio.normalize`
- Tag with `media.id`, `media.mime_type`, `media.size_bytes`, `media.owner_id`

### L-04: Metrics

- **Design**: Custom meters:
  - `media.uploads.initiated` (counter, by mime_type)
  - `media.uploads.completed` (counter, by mime_type)
  - `media.scans.passed` / `media.scans.failed` (counter)
  - `media.processing.duration` (histogram, by kind: transcode/image/audio)
  - `media.storage.bytes` (gauge, total active storage per owner)

### L-05: Soft-delete with retention

- **Design**: `DELETE /api/media/{id}` → sets `Status = Deleted`, `DeletedAt = now`. Background worker permanently deletes from S3 after retention period (default 30 days). GDPR erasure consumer deletes immediately.
- **Edge case**: File referenced by other services (Catalog product image) → publish `MediaDeletedEvent`, let consumers handle
- **Test spec**: `Delete_sets_soft_delete_fields`

---

## Migration Plan

### Phase 1: Fix Bugs (no new features)
1. BUG-01: Fix composite index migration
2. BUG-02: Fix unit test constructors
3. BUG-03: Add large-file scan support
4. BUG-04: Add xmin concurrency token
5. BUG-05: Add state transition guards
6. H-06: Remove OwnerId from public DTO

### Phase 2: Events & Notifications
1. C-03: Create `src/Contracts/Media/MediaEvents.cs`
2. H-04: Add MassTransit + outbox to Media service
3. C-04: Add notification consumers + templates
4. Architecture guard: `Media_events_extend_DomainEvent`

### Phase 3: Multipart Uploads
1. C-02: Multipart upload endpoints + S3 service extensions
2. H-05: Extended state machine
3. M-01: Upload sweeper worker
4. M-04: MIME type allowlist
5. M-05: Rate limiting

### Phase 4: S3 Event Notifications
1. C-01: SQS consumer for S3 events
2. LocalStack SQS in docker-compose
3. Integration test with LocalStack

### Phase 5: Processing Pipeline
1. H-01: Video transcoding (FFmpeg + HLS)
2. H-02: Image optimization (ImageSharp)
3. H-03: Audio normalization (FFmpeg)
4. M-02: Processing retry
5. M-03: Presigned GET URLs for variants

### Phase 6: Operational Excellence
1. M-06: List/query endpoint
2. L-01: ClamAV health check
3. L-02: S3 health check
4. L-03: OpenTelemetry spans
5. L-04: Metrics
6. L-05: Soft-delete with retention

---

## Architecture Guards to Add

| Guard | Pattern |
|---|---|
| `Media_events_extend_DomainEvent` | All records in `Contracts.Media` must extend `DomainEvent` |
| `Media_state_transitions_are_guarded` | `MediaFile.MarkAs*` methods must throw on invalid source state |
| `No_OwnerId_in_public_DTOs` | Response records must not expose `OwnerId` |
| `Media_uploads_validate_mime_type_allowlist` | `InitiateUploadValidator` must check MIME type |
| `Processing_workers_have_timeout` | FFmpeg/ImageSharp calls must have CancellationToken with timeout |
| `S3_multipart_uploads_have_sweeper` | If `InitiateMultipartUploadAsync` exists, sweeper worker must exist |

---

## Dependencies to Add

| Package | Purpose | Version |
|---|---|---|
| `SixLabors.ImageSharp` | Image resize, format conversion, EXIF | 3.1.x |
| `SixLabors.ImageSharp.Web` | (optional) On-the-fly resize middleware | 3.1.x |
| `AWSSDK.SQS` | S3 event notification polling | 3.7.x |
| `Blurhash.ImageSharp` | Progressive loading placeholders | 3.x |
| `MassTransit` | Event bus + outbox | 8.3.x |
| `MassTransit.RabbitMQ` | Transport | 8.3.x |
| `MassTransit.EntityFrameworkCore` | Outbox persistence | 8.3.x |

FFmpeg is an OS-level dependency (installed in Docker image), not a NuGet package.

---

## Docker Image Changes

```dockerfile
# Add to Media service Dockerfile
RUN apt-get update && apt-get install -y --no-install-recommends \
    ffmpeg \
    && rm -rf /var/lib/apt/lists/*
```

## LocalStack Changes

```yaml
# Add to docker-compose.yml localstack-init
awslocal sqs create-queue --queue-name media-upload-events
awslocal s3api put-bucket-notification-configuration \
  --bucket media-dev \
  --notification-configuration '{
    "QueueConfigurations": [{
      "QueueArn": "arn:aws:sqs:us-east-1:000000000000:media-upload-events",
      "Events": ["s3:ObjectCreated:*"]
    }]
  }'
```
