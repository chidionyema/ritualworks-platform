# Content Service

## Overview

The Content service is the bounded context responsible for all user-generated file management on the platform. It owns the full lifecycle of a uploaded file: intake, virus scanning, checksum validation, storage, and soft deletion. No file bytes ever traverse the API server; instead, the service mints presigned S3 URLs and the client uploads directly to the object store (Tigris in production, LocalStack in development). This design keeps the API process out of the byte path and allows arbitrarily large uploads without memory pressure.

Bounded context: **Content** — isolated from all other services. Other services reference content only by opaque `ContentId` (GUID). There are no cross-context foreign keys in the database.

---

## Architecture

The service follows standard Clean Architecture layering:

| Layer | Project | Responsibility |
|---|---|---|
| Domain | `Content.Domain` | `ContentEntity` aggregate, `ContentStatus` state machine, value objects, repository interface |
| Application | `Content.Application` | MediatR commands/queries, `IContentStorageService` interface, `IVirusScanner` interface, `StorageOptions`, FluentValidation validators |
| Infrastructure | `Content.Infrastructure` | `S3ContentStorageService` (AWS SDK v3), `ClamAVScanner`, `UploadSweeperService`, EF Core `ContentDbContext`, Polly resilience |
| API | `Content.Api` | `ContentController`, JWT authentication, Swagger |

**Key dependencies:**
- **MediatR** — command/query dispatch
- **AWS SDK for .NET (AWSSDK.S3)** — S3-compatible storage (Tigris / LocalStack / AWS)
- **ClamAV REST API** — virus scanning (HTTP, not the native ClamAV protocol)
- **EF Core 9 + Npgsql** — persistence
- **Polly** — retry + circuit breaker + bulkhead around every S3 call
- **Serilog** — structured logging
- **OpenTelemetry** (`ContentActivities` source) — distributed tracing on upload completion

---

## Domain Model

### Aggregate: `ContentEntity`

The central aggregate. Created via `ContentEntity.CreatePending(...)` (factory enforces invariants). All state transitions are guarded methods that throw `InvalidOperationException` on invalid source states.

**Properties:**

| Property | Type | Description |
|---|---|---|
| `Id` | `Guid` | Surrogate primary key |
| `EntityId` | `Guid` | ID of the owning entity in another service (e.g. a product) |
| `EntityType` | `string` | Type name of the owning entity (opaque string; no FK) |
| `OwnerUserId` | `string` | Identity of the uploading user |
| `FileName` | `string` | Original file name (sanitised) |
| `ContentTypeMime` | `string` | MIME type as declared by the client |
| `ContentType` | `enum` | Classified as `Image`, `Video`, `Document`, or `Other` |
| `UploadKind` | `enum` | `Single` (presigned PUT) or `Multipart` (S3 multipart) |
| `Status` | `ContentStatus` | Current lifecycle state (see below) |
| `S3UploadId` | `string?` | S3 multipart upload ID; null for single-PUT |
| `BucketName` | `string` | S3 bucket |
| `ObjectName` | `string` | S3 object key (per-user prefixed: `{userId}/{uuid}/{filename}`) |
| `ETag` | `string` | S3 ETag set on `MarkAvailable` |
| `Sha256Checksum` | `string?` | SHA-256 of the object bytes, computed server-side after upload |
| `FileSize` | `long` | Declared size at init; replaced with actual size on completion |
| `ValidatedAt` | `DateTime?` | Timestamp of successful validation |
| `QuarantineReason` | `string?` | Set when ClamAV detects a threat |
| `FailureReason` | `string?` | Set for non-quarantine terminal failures |

**Navigation:** `Metadata` (`ContentMetadata[]`), `Versions` (`ContentVersion[]`) — one-to-many, cascade-deleted.

### Enum: `ContentStatus`

```
Pending -> Validating -> Available
                      -> Quarantined   (virus / magic-byte mismatch; S3 object moved to quarantine/ prefix)
                      -> Failed        (S3 error, checksum failure; retryable in principle)
Available -> Deleted   (soft delete; S3 object deletion is best-effort)
```

Stuck `Pending` rows are expired by `UploadSweeperService` after the configured TTL (default: 6 hours).

### Value Objects

| Type | Description |
|---|---|
| `ChunkSession` | Tracks multipart chunk progress (uploaded chunk indices, expiry) |
| `FileValidationResult` | Result of file signature validation — `IsValid`, `Errors`, `FileType` |
| `VirusScanResult` | ClamAV scan result — `IsMalicious`, `ThreatName` |
| `FileSignatureValidationResult` | Magic-byte check result |

### Invariants enforced at the aggregate boundary

- `UploadKind.Multipart` requires a non-null `S3UploadId`.
- `ExpectedSize` must be positive.
- `entityType`, `ownerUserId`, `fileName`, `bucketName`, `objectKey` must not be empty.
- A `Quarantined` or `Deleted` row cannot be quarantined again.
- An `Available`, `Deleted`, or `Quarantined` row cannot be `Fail`-ed.
- Only `Pending` rows can enter `Validating`.

---

## API Endpoints

All endpoints are under the `ContentUploader` authorization policy, which requires a valid JWT with role `ContentUploader` or `Admin`. User identity is extracted from the forwarded `X-User-Id` header, set by the BFF/gateway.

| Method | Route | Auth | Description |
|---|---|---|---|
| `POST` | `/api/v1/content/uploads` | `ContentUploader` | Initiate an upload. Returns presigned PUT URL (single) or S3 multipart upload ID + per-part presigned URLs. Creates a `Pending` `ContentEntity`. |
| `POST` | `/api/v1/content/uploads/{contentId}/complete` | `ContentUploader` | Signal upload completion. For multipart, stitches parts; for single, reads object HEAD. Runs ClamAV scan and file-signature validation. Transitions to `Available` or `Quarantined`. |
| `POST` | `/api/v1/content/uploads/{contentId}/abort` | `ContentUploader` | Cancel an in-flight upload. Aborts S3 multipart (if applicable) and marks the row `Failed`. Idempotent. |
| `GET` | `/api/v1/content/uploads/{contentId}` | `ContentUploader` | Poll upload status. Returns `ContentStatus`, `FailureReason`, `QuarantineReason`, `ValidatedAt`. |
| `GET` | `/api/v1/content/{id}` | `ContentUploader` | Retrieve content metadata and a presigned GET URL (15-minute TTL). |
| `DELETE` | `/api/v1/content/{id}` | `ContentUploader` | Soft-delete: marks the row `Deleted`, then best-effort deletes the S3 object. |

**Upload kind selection:** Files at or below `Storage:SinglePutMaxBytes` (default 8 MiB) use a single presigned PUT. Larger files trigger S3 multipart with 5 MiB minimum part size.

**Presigned URL TTL:** Default 15 minutes for both upload and download (configurable).

---

## Events

The Content service does not publish integration events via the message bus in the current implementation. Lifecycle transitions are tracked exclusively in the Content database. Other services that need to know when content becomes available should poll `GET /api/v1/content/{id}` or subscribe to future CDC events on the `content.public.Contents` table.

No events are consumed from other services.

---

## Configuration

All configuration uses the standard .NET `IOptions<T>` pattern with `ValidateOnStart()`. Missing required values fail the host on startup.

### `Storage` section (`StorageOptions`)

| Key | Type | Required | Default | Description |
|---|---|---|---|---|
| `Storage:ServiceUrl` | `string` (URL) | Yes | — | S3 endpoint URL. Use `http://localhost:4566` for LocalStack, `https://fly.storage.tigris.dev` for Tigris. |
| `Storage:AccessKey` | `string` | Yes | — | S3 access key |
| `Storage:SecretKey` | `string` | Yes | — | S3 secret key |
| `Storage:BucketName` | `string` | Yes | — | Bucket for all uploads |
| `Storage:Region` | `string` | No | `auto` | `auto` for Tigris/R2, explicit region for AWS |
| `Storage:ForcePathStyle` | `bool` | No | `false` | Required for LocalStack |
| `Storage:PresignedUploadTtl` | `TimeSpan` | No | `00:15:00` | Upload URL TTL |
| `Storage:PresignedDownloadTtl` | `TimeSpan` | No | `00:15:00` | Download URL TTL |
| `Storage:SinglePutMaxBytes` | `long` | No | `8388608` | Files above this use multipart |
| `Storage:PendingUploadTtl` | `TimeSpan` | No | `06:00:00` | Sweeper expiry for stuck Pending rows |
| `Storage:SweepInterval` | `TimeSpan` | No | `00:05:00` | Sweeper poll interval |

### `ClamAV` section (`ClamAvOptions`)

| Key | Type | Required | Default | Description |
|---|---|---|---|---|
| `ClamAV:RestApiUrl` | `string` (URL) | Yes | — | ClamAV REST API endpoint (e.g. `http://clamav:8080/scan`) |
| `ClamAV:TimeoutSeconds` | `int` | No | `30` | Per-scan HTTP timeout |

### `MinIO` section (`MinioOptions`)

Legacy section retained for backward compatibility. Not used by `S3ContentStorageService` (which reads from `Storage`). Kept for tooling that reads this section directly.

| Key | Type | Required | Description |
|---|---|---|---|
| `MinIO:Endpoint` | `string` | Yes | MinIO/LocalStack endpoint |
| `MinIO:AccessKey` | `string` | Yes | Access key |
| `MinIO:SecretKey` | `string` | Yes | Secret key |
| `MinIO:BucketName` | `string` | Yes | Bucket name |
| `MinIO:Secure` | `bool` | No | Use HTTPS (default: `true`) |

### Connection strings

| Key | Description |
|---|---|
| `ConnectionStrings:Content` | PostgreSQL connection string for the Content database |

### Platform configuration (inherited via `AddServiceDefaults`)

| Key | Description |
|---|---|
| `Authentication:JwksUri` | JWKS endpoint for JWT validation |
| `Serilog:*` | Serilog configuration |

---

## Database

**Schema:** `content`

**Tables:**

| Table | Description |
|---|---|
| `content.Contents` | Primary aggregate table. One row per upload intent. Indexed on `(EntityId, EntityType)`, `(Status, CreatedAt)`, `OwnerUserId`, `Slug`. Uses PostgreSQL `xmin` as optimistic concurrency token. |
| `content.ContentMetadata` | Key-value metadata pairs owned by a `ContentEntity`. Unique index on `(ContentId, Key)`. |
| `content.ContentVersions` | Version history entries owned by a `ContentEntity`. |
| `__EFMigrationsHistory` | EF Core migration tracking (default schema). |

**Migrations:**

| Migration | Description |
|---|---|
| `20260504094652_InitialContentSchema` | Initial schema: `Contents`, `ContentMetadata`, `ContentVersions` |
| `20260509043941_ContentLifecycleAndS3Multipart` | Adds lifecycle columns (`Status`, `UploadKind`, `S3UploadId`, `Sha256Checksum`, `QuarantineReason`, `FailureReason`, `ValidatedAt`) and sweeper index on `(Status, CreatedAt)` |

EF migrations are applied automatically at startup via `MigrateWithRetryAsync` (skipped in `Test` environment, where the integration test fixture controls schema lifecycle).

---

## Testing

### Test projects

| Project | Path | Description |
|---|---|---|
| `Content.Unit` | `tests/Content/Content.Unit` | Unit tests for domain logic, command handlers, validators |
| `Content.Integration` | `tests/Content/Content.Integration` | Integration tests against real Postgres + LocalStack |
| `Content.Architecture` | `tests/Content/Content.Architecture` | Architecture guard tests (dependency rules) |
| `Content.Contract` | `tests/Content/Content.Contract` | Contract/consumer-driven tests |

### Running tests

```bash
# Unit tests only (no external dependencies)
dotnet test tests/Content/Content.Unit

# Integration tests (requires Docker — containers are managed by shared Testcontainers singletons)
dotnet test tests/Content/Content.Integration

# All Content tests
dotnet test tests/Content/
```

### Integration test infrastructure

Integration tests use shared Testcontainers singletons from `BuildingBlocks.Testing.Containers`. Do not create raw `PostgreSqlBuilder` or `ContainerBuilder` instances in test projects — the CI architecture check will fail.

- **Postgres:** `SharedTestPostgres.CreateDatabaseAsync("content")`
- **S3 / LocalStack:** provisioned via the shared container infrastructure

The `ContentDbContext` migration is driven by the test fixture, not `Program.cs`, in the `Test` environment.
