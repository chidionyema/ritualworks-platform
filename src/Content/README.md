# Content Service

Manages user-uploaded files using S3-compatible object storage with virus scanning and multipart upload lifecycle management.

## Responsibilities
- Orchestrate S3 multipart upload: init → upload parts → complete/abort
- Scan uploads with ClamAV; quarantine infected files
- Track upload state machine: Pending → Validating → Available / Quarantined / Failed / Deleted
- Sweep stale in-progress uploads via `UploadSweeperService`

## API Endpoints
| Method | Route | Notes |
|--------|-------|-------|
| POST | `/api/content/upload/init` | Start multipart upload |
| POST | `/api/content/upload/complete` | Complete multipart upload |
| POST | `/api/content/upload/abort` | Abort multipart upload |
| GET | `/api/content/upload/{uploadId}/status` | Upload status |
| GET | `/api/content/{id}` | Get content (presigned URL) |
| DELETE | `/api/content/{id}` | Soft-delete content |

## Domain Entities
- **ContentEntity** — aggregate with state machine; tracks `S3UploadId`, `ETag`, `Sha256Checksum`, `ContentType`, `SizeBytes`

## Events Published
- `ContentAvailableEvent`
- `ContentDeletedEvent`

## Infrastructure Dependencies
- PostgreSQL (`ContentDbContext`) with Vault dynamic credentials
- S3-compatible storage (`AmazonS3Client`) via presigned URLs
- ClamAV virus scanner
- RabbitMQ via MassTransit (transactional outbox)

## Configuration
```
ConnectionStrings:content
Vault:Enabled / RoleId / SecretId
Aws:Region / BucketName / AccessKey / SecretKey
ClamAv:Host / Port
RabbitMq:Host / Username / Password
```

## Health Checks
- DB: `AddDbHealthCheck<ContentDbContext>()`
