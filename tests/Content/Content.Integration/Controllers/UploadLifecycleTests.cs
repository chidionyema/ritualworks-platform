using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;
using FluentAssertions;
using Haworks.Content.Application.DTOs;
using Haworks.Content.Domain.Entities;
using Haworks.Content.Infrastructure.BackgroundServices;
using Haworks.Content.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haworks.Content.Integration.Controllers;

/// <summary>
/// End-to-end lifecycle coverage for the presigned-URL upload pipeline.
/// Complements <see cref="UploadFlowTests"/> by exercising the parts of
/// the pipeline that go past the presign mint:
///   1. Init → PUT → Complete → row Available + bytes downloadable (single PUT)
///   2. Init → PUT-each-part → Complete → row Available + SHA-256 round-trip (multipart)
///   3. Sweeper aborts an abandoned multipart upload (S3 + DB)
///   4. Quarantine: virus-scan failure moves the object to quarantine/ and the row to Quarantined
/// </summary>
public sealed class UploadLifecycleTests : IClassFixture<ContentWebAppFactory>, IAsyncLifetime
{
    private readonly ContentWebAppFactory _factory;
    private readonly HttpClient _api;
    private readonly HttpClient _bare;

    public UploadLifecycleTests(ContentWebAppFactory factory)
    {
        _factory = factory;
        _api = factory.CreateClient();
        // Bare client PUTs go directly to LocalStack via the presigned URL,
        // bypassing the in-process TestServer (which would refuse the
        // S3-bound host).
        _bare = new HttpClient();
    }

    public Task InitializeAsync() => _factory.EnsureSchemaAsync();

    public Task DisposeAsync()
    {
        // Reset the virus-scanner flag in case Test 4 left it tripped — the
        // fixture is shared by all tests in this class so a leak between
        // tests would corrupt subsequent runs.
        _factory.VirusScanShouldFail = false;
        return Task.CompletedTask;
    }

    // ----------------------------------------------------------------
    // Test 1 — Single-PUT full round-trip
    // ----------------------------------------------------------------
    [Fact]
    public async Task Single_put_full_lifecycle_lands_Available_and_bytes_are_downloadable()
    {
        var fileBytes = MakeFakePngBytes(8 * 1024);
        var entityId = Guid.NewGuid();

        // 1. Init
        var initResp = await _api.PostAsJsonAsync("/api/v1/content/uploads", new InitUploadRequestDto(
            EntityId: entityId,
            EntityType: "Product",
            FileName: "lifecycle-single.png",
            ContentType: "image/png",
            TotalSize: fileBytes.Length));
        initResp.EnsureSuccessStatusCode();
        var init = await initResp.Content.ReadFromJsonAsync<InitUploadResultDto>();
        init.Should().NotBeNull();
        init!.Kind.Should().Be(UploadKind.Single);

        // 2. PUT bytes directly to S3 via the presigned URL.
        using (var put = new HttpRequestMessage(HttpMethod.Put, init.PutUrl))
        {
            put.Content = new ByteArrayContent(fileBytes);
            put.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            (await _bare.SendAsync(put)).EnsureSuccessStatusCode();
        }

        // 3. Complete (no parts list for single-PUT).
        var completeResp = await _api.PostAsJsonAsync(
            $"/api/v1/content/uploads/{init.ContentId}/complete",
            new CompleteUploadRequestDto(Parts: null));
        completeResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var status = await completeResp.Content.ReadFromJsonAsync<UploadStatusDto>();
        status.Should().NotBeNull();
        status!.Status.Should().Be(ContentStatus.Available,
            "a single-PUT with valid PNG bytes + clean scan should finalise as Available");

        // 4. GET /api/v1/content/{id} → ContentDto with non-empty DownloadUrl.
        var contentResp = await _api.GetAsync($"/api/v1/content/{init.ContentId}");
        contentResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await contentResp.Content.ReadFromJsonAsync<ContentDto>();
        dto.Should().NotBeNull();
        dto!.DownloadUrl.Should().NotBeNullOrEmpty();

        // 5. Bare GET on the presigned URL returns the exact bytes.
        var downloaded = await _bare.GetByteArrayAsync(dto.DownloadUrl);
        downloaded.Should().Equal(fileBytes,
            "the bytes returned via the presigned GET must match what was uploaded");
    }

    // ----------------------------------------------------------------
    // Test 2 — Multipart full round-trip
    // ----------------------------------------------------------------
    [Fact]
    public async Task Multipart_full_lifecycle_returns_Available_and_round_trips_sha256()
    {
        // 12 MiB > the 8 MiB SinglePutMaxBytes default → multipart route.
        // Stays small enough that LocalStack handles it in a couple of seconds.
        const int totalSize = 12 * 1024 * 1024;
        const int partSize = 5 * 1024 * 1024;
        var fileBytes = MakeFakePngBytes(totalSize);
        var entityId = Guid.NewGuid();

        // 1. Init returns multipart shape.
        var initResp = await _api.PostAsJsonAsync("/api/v1/content/uploads", new InitUploadRequestDto(
            EntityId: entityId,
            EntityType: "Product",
            FileName: "lifecycle-multi.png",
            ContentType: "image/png",
            TotalSize: totalSize));
        initResp.EnsureSuccessStatusCode();
        var init = await initResp.Content.ReadFromJsonAsync<InitUploadResultDto>();
        init.Should().NotBeNull();
        init!.Kind.Should().Be(UploadKind.Multipart);
        init.PartUrls.Should().NotBeNullOrEmpty();

        // 2. PUT each part via the presigned URL; capture ETags.
        var uploadedParts = new List<UploadedPartDto>();
        for (var i = 0; i < init.PartUrls!.Count; i++)
        {
            var offset = i * partSize;
            var thisPartSize = Math.Min(partSize, fileBytes.Length - offset);
            if (thisPartSize <= 0) break;

            var partBytes = new byte[thisPartSize];
            Array.Copy(fileBytes, offset, partBytes, 0, thisPartSize);

            using var put = new HttpRequestMessage(HttpMethod.Put, init.PartUrls[i].Url)
            {
                Content = new ByteArrayContent(partBytes),
            };
            // No Content-Type header — the per-part presign in
            // S3ContentStorageService.InitMultipartUploadAsync does NOT
            // sign one, so adding one here would invalidate the signature.
            var partResp = await _bare.SendAsync(put);
            partResp.EnsureSuccessStatusCode();

            // S3 returns ETag in the response headers; strip surrounding
            // quotes — the AWS SDK includes them on the way in but the
            // CompleteMultipartUpload PartETags accept either.
            var etag = partResp.Headers.ETag?.Tag
                ?? partResp.Headers.GetValues("ETag").FirstOrDefault()
                ?? throw new InvalidOperationException(
                    $"S3 did not return an ETag for part {i + 1}");
            uploadedParts.Add(new UploadedPartDto(init.PartUrls[i].PartNumber, etag.Trim('"')));
        }

        uploadedParts.Should().HaveCount(init.PartUrls.Count);

        // 3. Complete with the per-part ETag list.
        var completeResp = await _api.PostAsJsonAsync(
            $"/api/v1/content/uploads/{init.ContentId}/complete",
            new CompleteUploadRequestDto(Parts: uploadedParts));
        completeResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var status = await completeResp.Content.ReadFromJsonAsync<UploadStatusDto>();
        status!.Status.Should().Be(ContentStatus.Available,
            "all parts were uploaded with valid ETags and the scan returned clean");

        // 4. Download via the presigned GET URL and verify SHA-256.
        var contentResp = await _api.GetAsync($"/api/v1/content/{init.ContentId}");
        contentResp.EnsureSuccessStatusCode();
        var dto = await contentResp.Content.ReadFromJsonAsync<ContentDto>();
        var downloaded = await _bare.GetByteArrayAsync(dto!.DownloadUrl);

        downloaded.Length.Should().Be(fileBytes.Length);
        Sha256Hex(downloaded).Should().Be(Sha256Hex(fileBytes),
            "the stitched object must hash to the same bytes the client uploaded");
    }

    // ----------------------------------------------------------------
    // Test 3 — Sweeper aborts an abandoned multipart upload
    // ----------------------------------------------------------------
    [Fact]
    public async Task Sweeper_aborts_orphan_multipart_and_marks_row_Failed()
    {
        // 32 MiB → multipart, > SinglePutMaxBytes.
        const long totalSize = 32L * 1024 * 1024;
        var initResp = await _api.PostAsJsonAsync("/api/v1/content/uploads", new InitUploadRequestDto(
            EntityId: Guid.NewGuid(),
            EntityType: "Product",
            FileName: "abandoned.bin",
            ContentType: "application/octet-stream",
            TotalSize: totalSize));
        initResp.EnsureSuccessStatusCode();
        var init = await initResp.Content.ReadFromJsonAsync<InitUploadResultDto>();
        init.Should().NotBeNull();
        init!.Kind.Should().Be(UploadKind.Multipart);
        init.UploadId.Should().NotBeNullOrEmpty();

        // Confirm the multipart upload exists in S3 before the sweep.
        var s3 = BuildSideChannelS3();
        var beforeUploads = await s3.ListMultipartUploadsAsync(new ListMultipartUploadsRequest
        {
            BucketName = _factory.Bucket,
        });
        beforeUploads.MultipartUploads
            .Any(u => u.UploadId == init.UploadId).Should().BeTrue(
                "the multipart upload should be live in S3 right after Init");

        // Manually shift CreatedAt back past the sweeper's TTL — the
        // DbContext only forces CreatedAt = UtcNow on Added rows, so a
        // Modified update will stick.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ContentDbContext>();
            // Default PendingUploadTtl is 6h; shift well past that.
            await db.Contents
                .Where(c => c.Id == init.ContentId)
                .ExecuteUpdateAsync(s => s.SetProperty(
                    c => c.CreatedAt,
                    c => DateTime.UtcNow.AddDays(-1)));
        }

        // Invoke the sweeper directly — bypasses the periodic timer so the
        // test does not have to sleep on SweepInterval.
        using (var scope = _factory.Services.CreateScope())
        {
            var sweeper = scope.ServiceProvider.GetRequiredService<UploadSweeperService>();
            await sweeper.SweepOnceAsync(CancellationToken.None);
        }

        // Row is now Failed with reason mentioning "abandoned".
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ContentDbContext>();
            var row = await db.Contents.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == init.ContentId);
            row.Should().NotBeNull();
            row!.Status.Should().Be(ContentStatus.Failed);
            row.FailureReason.Should().NotBeNull();
            row.FailureReason!.Should().Contain("abandoned",
                "the sweeper's failure reason must surface why the row was reaped");
        }

        // The S3 multipart upload should no longer exist.
        var afterUploads = await s3.ListMultipartUploadsAsync(new ListMultipartUploadsRequest
        {
            BucketName = _factory.Bucket,
        });
        afterUploads.MultipartUploads
            .Any(u => u.UploadId == init.UploadId).Should().BeFalse(
                "AbortMultipartUpload should have torn down the abandoned upload in S3");
    }

    // ----------------------------------------------------------------
    // Test 4 — Quarantine flow on virus-scan failure
    // ----------------------------------------------------------------
    [Fact]
    public async Task Quarantine_flow_moves_object_under_quarantine_prefix_and_row_to_Quarantined()
    {
        _factory.VirusScanShouldFail = true;
        try
        {
            var fileBytes = MakeFakePngBytes(4 * 1024);

            // 1. Init + PUT (single-PUT path is enough to exercise quarantine).
            var initResp = await _api.PostAsJsonAsync("/api/v1/content/uploads", new InitUploadRequestDto(
                EntityId: Guid.NewGuid(),
                EntityType: "Product",
                FileName: "infected.png",
                ContentType: "image/png",
                TotalSize: fileBytes.Length));
            initResp.EnsureSuccessStatusCode();
            var init = await initResp.Content.ReadFromJsonAsync<InitUploadResultDto>();

            using (var put = new HttpRequestMessage(HttpMethod.Put, init!.PutUrl))
            {
                put.Content = new ByteArrayContent(fileBytes);
                put.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
                (await _bare.SendAsync(put)).EnsureSuccessStatusCode();
            }

            // Capture the original object key from the DB before Complete
            // moves the object — we need it to assert the post-quarantine
            // S3 layout.
            string objectKey;
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ContentDbContext>();
                var row = await db.Contents.AsNoTracking()
                    .FirstAsync(c => c.Id == init.ContentId);
                objectKey = row.ObjectName;
            }

            // 2. Complete — virus scanner returns malicious → quarantine path.
            var completeResp = await _api.PostAsJsonAsync(
                $"/api/v1/content/uploads/{init.ContentId}/complete",
                new CompleteUploadRequestDto(Parts: null));
            completeResp.StatusCode.Should().Be(HttpStatusCode.OK);
            var status = await completeResp.Content.ReadFromJsonAsync<UploadStatusDto>();
            status!.Status.Should().Be(ContentStatus.Quarantined);
            status.QuarantineReason.Should().NotBeNullOrEmpty();

            // 3. The original object key should be gone, and the
            //    quarantine/{originalKey} object should exist.
            var s3 = BuildSideChannelS3();

            var originalExists = await ObjectExistsAsync(s3, objectKey);
            originalExists.Should().BeFalse(
                "the original object should have been deleted as part of the quarantine move");

            var quarantineExists = await ObjectExistsAsync(s3, "quarantine/" + objectKey);
            quarantineExists.Should().BeTrue(
                "the object should now live under the quarantine/ prefix");
        }
        finally
        {
            _factory.VirusScanShouldFail = false;
        }
    }

    // ----------------------------------------------------------------
    // Helpers
    // ----------------------------------------------------------------
    private AmazonS3Client BuildSideChannelS3()
    {
        // A direct AWS SDK client against LocalStack — used for assertions
        // that bypass the application (e.g. ListMultipartUploads, HEAD on
        // a quarantined key). Not pooled; tests dispose by GC.
        var cfg = new AmazonS3Config
        {
            ServiceURL = _factory.LocalstackUrl,
            ForcePathStyle = true,
            AuthenticationRegion = "us-east-1",
            UseHttp = _factory.LocalstackUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase),
        };
        return new AmazonS3Client("test", "test", cfg);
    }

    private async Task<bool> ObjectExistsAsync(IAmazonS3 s3, string key)
    {
        try
        {
            await s3.GetObjectMetadataAsync(_factory.Bucket, key);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private static byte[] MakeFakePngBytes(int sizeBytes)
    {
        var bytes = new byte[sizeBytes];
        // Real PNG magic — FileSignatureValidator's image/png entry checks
        // these eight bytes, so any tampering here will trip signature
        // validation and the test will go through the quarantine path
        // instead of Available.
        bytes[0] = 0x89; bytes[1] = (byte)'P'; bytes[2] = (byte)'N'; bytes[3] = (byte)'G';
        bytes[4] = 0x0D; bytes[5] = 0x0A; bytes[6] = 0x1A; bytes[7] = 0x0A;
        // Fill the remainder with a deterministic, non-zero pattern so a
        // SHA-256 round-trip actually compares meaningful content.
        for (var i = 8; i < bytes.Length; i++) bytes[i] = (byte)(i & 0xFF);
        return bytes;
    }

    private static string Sha256Hex(byte[] data)
    {
        var hash = SHA256.HashData(data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
