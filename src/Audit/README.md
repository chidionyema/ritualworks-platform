# Audit Service

## Overview

The Audit service is the bounded context responsible for capturing, storing, querying, and exporting a tamper-evident, compliance-grade audit trail of all domain events that occur across the platform. It is a passive observer: it subscribes to every `IDomainEvent` published on the RabbitMQ bus (via a generic `AuditConsumer<T>`), extracts structured fields, redacts secrets and PII-adjacent data, and writes rows into an append-only, monthly-partitioned PostgreSQL table using PostgreSQL `COPY` (binary mode) for high-throughput batched ingestion.

The service provides a read API for compliance officers and operators and an async export API for producing time-range extracts.

Bounded context: **Audit** — the service holds no primary business data. All content is derived from integration events emitted by other services. Rows are immutable once written; there is no update or delete path on `audit_events`.

---

## Architecture

| Layer | Project | Responsibility |
|---|---|---|
| Domain | `Audit.Domain` | `AuditEvent` entity — append-only row shape |
| Application | `Audit.Application` | `AuditConsumer<T>`, `AuditConsumerRegistry`, `IAuditWriter`, `IAuditExtractor<T>`, `ISecretRedactor`, export job interfaces, query interfaces |
| Infrastructure | `Audit.Infrastructure` | `AuditWriter` (channel-buffered COPY writer), `AuditQueryService`, `AuditExportJobService`, `AuditExportWorker`, `PartitionRolloverService`, `AuditDbContext` |
| API | `Audit.Api` | `AuditQueryController`, `AuditExportController`, JWT authentication |

**Key dependencies:**
- **MassTransit 8 + RabbitMQ** — generic consumer registration for all `IDomainEvent` types via reflection over the Contracts assembly
- **Npgsql binary COPY** — high-throughput batch insert into `audit_events` (bypasses EF insert overhead)
- **EF Core 9 + Npgsql** — used for query and export job tracking; not used for `audit_events` writes
- **`System.Threading.Channels`** — in-process bounded queue between MassTransit consumers and the COPY writer (batch size: 50 rows, 200 ms flush window)
- **Serilog** — structured logging

---

## Domain Model

### Entity: `AuditEvent`

The sole domain entity. Append-only; no mutation after `AuditEvent.Create(...)`.

| Property | Column | Type | Description |
|---|---|---|---|
| `Id` | `id` | `uuid` | Surrogate identifier |
| `OccurredAt` | `occurred_at` | `timestamptz` | When the originating event occurred (from the event itself) |
| `ReceivedAt` | `received_at` | `timestamptz` | When the Audit service wrote the row |
| `EventType` | `event_type` | `text` | Simple type name of the .NET event class (e.g. `StockReservationFailedEvent`) |
| `EntityType` | `entity_type` | `text` | Logical entity type (e.g. `order`, `product`, `system`) |
| `EntityId` | `entity_id` | `text` | Opaque identifier of the entity |
| `ActorId` | `actor_id` | `text?` | Identity of the actor who triggered the event |
| `ActorType` | `actor_type` | `text?` | Type of the actor (e.g. `user`, `system`) |
| `CorrelationId` | `correlation_id` | `text?` | MassTransit correlation ID for request tracing |
| `Payload` | `payload` | `jsonb` | Full serialized event, with secrets redacted |
| `Metadata` | `metadata` | `jsonb` | Transport metadata: `message_id`, `rabbitMqRoutingKey`, `publishedBy` |

**Composite primary key:** `(id, occurred_at)` — required because the `audit_events` table is range-partitioned by `occurred_at` and PostgreSQL requires the partition key in the primary key of partitioned tables.

**Idempotency:** The `message_id` extracted from `metadata` has a unique index per monthly partition (`audit_events_msg_id_uniq_{year}_{month}`). Duplicate deliveries from MassTransit are silently discarded at the database level.

### Entity: `AuditExportJob` (Infrastructure)

Tracks async export job state.

| Property | Type | Description |
|---|---|---|
| `Id` | `Guid` | Job identifier |
| `Status` | `AuditExportStatus` | `Queued`, `Running`, `Succeeded`, `Failed` |
| `RequestedBy` | `string` | Username of the requester |
| `RequestJson` | `jsonb` | Serialized `AuditExportRequest` (filter parameters) |
| `StartedAt` | `DateTimeOffset?` | When the worker began processing |
| `CompletedAt` | `DateTimeOffset?` | When the worker finished |
| `DownloadUrl` | `string?` | Pre-signed download URL of the export file |
| `Error` | `string?` | Error message if `Failed` |

---

## Capture Pipeline

1. **`AuditConsumerRegistry`** enumerates every concrete `IDomainEvent` implementation in the Contracts assembly at startup and registers a generic `AuditConsumer<T>` for each type via MassTransit reflection.

2. **`AuditConsumer<T>.Consume`** (per event type):
   - Calls the registered `IAuditExtractor<T>` to produce an `AuditRow`.
   - Passes the payload through `ISecretRedactor.Redact`.
   - Enriches metadata with `message_id` (for idempotency), `rabbitMqRoutingKey`, and `publishedBy`.
   - Enqueues the `AuditRow` onto the in-process `Channel<AuditRow>` (non-blocking).

3. **`AuditWriter`** (singleton background worker):
   - Reads batches of up to 50 rows from the channel every 200 ms.
   - Opens a `NpgsqlConnection` and issues a binary `COPY audit_events FROM STDIN (FORMAT BINARY)` for each batch.
   - On shutdown, flushes all remaining rows before disposing.

### Extractors

| Extractor | Registered For | `EntityType` / `EntityId` |
|---|---|---|
| `StockReservationFailedExtractor` | `StockReservationFailedEvent` | `order` / `evt.OrderId` |
| `VaultRotationStageExtractor` | `VaultRotationStageEvent` | `system` / `identity-svc` |
| `ReflectionAuditExtractor<T>` | All other event types (fallback) | Discovers entity ID by inspecting well-known property names: `OrderId`, `UserId`, `PaymentId`, `SkuId`, `ProductId`, `CartId` |

Custom extractors are registered by implementing `IAuditExtractor<T>` and registering via DI. They take precedence over the reflection fallback.

### Redaction (`SecretRedactor`)

The redactor walks the `Payload` JSON tree before writing and:
- **Removes** any JSON property whose key ends with: `token`, `password`, `secret`, `key`, `credential`, `apikey`, `authorization` (case-insensitive).
- **Removes** CVV/CVC fields: `cvv`, `cvc`, `securityCode`.
- **Replaces** `RawBody` fields with `RawBodySha256` (SHA-256 of the raw value).
- **Masks** credit card numbers in string values using Luhn-validated regex: replaces with `****{last4}`.

---

## API Endpoints

### `AuditQueryController` — `/audit/events`

Requires role `audit-reader`.

| Method | Route | Auth | Description |
|---|---|---|---|
| `GET` | `/audit/events` | `audit-reader` | Paginated query of audit events. Supports cursor-based pagination (opaque base64 cursor encoding `occurredAt` + `id`). Results ordered by `occurred_at DESC, id DESC`. |
| `GET` | `/audit/events/{id}` | `audit-reader` | Fetch a single event by ID. Requires `occurredAt` query parameter (needed to route to the correct partition). |

**Query parameters for `GET /audit/events`:**

| Parameter | Description |
|---|---|
| `entityType` | Filter by entity type |
| `entityId` | Filter by entity ID |
| `eventType` | Filter by event type name |
| `from` | Inclusive lower bound on `occurred_at` |
| `to` | Inclusive upper bound on `occurred_at` |
| `cursor` | Opaque pagination cursor from previous response |
| `limit` | Results per page (default: 50) |

### `AuditExportController` — `/audit/export`

| Method | Route | Auth | Description |
|---|---|---|---|
| `POST` | `/audit/export` | `audit-admin` | Enqueue an async export job. Returns `{ jobId, status: "queued" }` with HTTP 202. |
| `GET` | `/audit/export/{jobId}` | `audit-reader` | Poll export job status. Returns job snapshot including `status`, `startedAt`, `completedAt`, `downloadUrl`, `error`. |

**`POST /audit/export` request body (`AuditExportRequest`):**

| Field | Type | Description |
|---|---|---|
| `entityId` | `string?` | Filter by entity ID |
| `entityType` | `string?` | Filter by entity type |
| `eventType` | `string?` | Filter by event type |
| `from` | `DateTimeOffset` | Start of export range (required) |
| `to` | `DateTimeOffset` | End of export range (required) |

---

## Events

### Consumed

The Audit service consumes **every** `IDomainEvent` published across the platform. The full list is determined at startup by enumerating the `Haworks.Contracts` assembly. Notable events consumed include (non-exhaustive):

| Event | Published By |
|---|---|
| `StockReservationFailedEvent` | Catalog / CheckoutOrchestrator |
| `VaultRotationStageEvent` | Identity |
| `ProductCacheInvalidatedEvent` | Catalog |
| All saga state-change events | CheckoutOrchestrator, Payments |
| All payment/refund events | Payments |
| All order events | Orders |

### Published

The Audit service publishes no events.

---

## Configuration

### Connection strings

| Key | Description |
|---|---|
| `ConnectionStrings:audit` | PostgreSQL connection string for the Audit database |
| `ConnectionStrings:rabbitmq` | RabbitMQ AMQP URI (default: `amqp://guest:guest@localhost:5672/`) |

### Platform configuration (inherited)

| Key | Description |
|---|---|
| `Authentication:JwksUri` | JWKS endpoint for JWT validation |

No additional service-specific configuration sections are required. The `AuditDbContext` connection string is provided directly from `ConnectionStrings:audit`.

---

## Database

**Schema:** (default — no explicit schema prefix on `audit_events`)

**Tables:**

| Table | Type | Description |
|---|---|---|
| `audit_events` | Partitioned (range on `occurred_at`) | Append-only event log. Monthly child partitions created by `PartitionRolloverService`. |
| `audit_events_{year}_{month}` | Child partition | Monthly partition (e.g. `audit_events_2026_05`). Each partition has indexes on `(entity_type, entity_id, occurred_at DESC)`, `(event_type, occurred_at DESC)`, and a unique index on `metadata->>'message_id'`. |
| `audit_export_jobs` | Regular table | Export job tracking. |
| `__EFMigrationsHistory` | Regular table | EF Core migration tracking. |

**Migrations:**

| Migration | Description |
|---|---|
| `20260510144352_AddAuditEventsPartitioned` | Creates the partitioned `audit_events` parent table and initial partitions |
| `20260510145746_AddAuditExportJobs` | Creates the `audit_export_jobs` table |

EF migrations are applied at startup via `db.Database.MigrateAsync()` (skipped in `Test` environment).

### Partition management

`PartitionRolloverService` is a `BackgroundService` that runs daily and ensures the current month's and next month's child partitions exist. Each partition is created using `CREATE TABLE IF NOT EXISTS ... PARTITION OF audit_events FOR VALUES FROM ... TO ...`. Partition creation is idempotent.

### Write path

Writes to `audit_events` bypass EF Core's insert path entirely. `AuditWriter` uses Npgsql's `BeginBinaryImportAsync` to issue binary `COPY FROM STDIN` statements directly. This avoids ORM overhead for high-volume event ingestion.

---

## Testing

### Test projects

| Project | Path | Description |
|---|---|---|
| `Audit.Unit` | `tests/Audit/Audit.Unit` | Unit tests for `SecretRedactor`, extractors, `AuditConsumer<T>` pipeline, export status |
| `Audit.Integration` | `tests/Audit/Audit.Integration` | Integration tests against real Postgres; validates COPY writes, partition creation, query pagination, export job lifecycle |

### Running tests

```bash
# Unit tests (no external dependencies)
dotnet test tests/Audit/Audit.Unit

# Integration tests (requires Docker)
dotnet test tests/Audit/Audit.Integration

# All Audit tests
dotnet test tests/Audit/
```

### Integration test infrastructure

Integration tests use the shared Testcontainers singleton:

```csharp
var db = await SharedTestPostgres.CreateDatabaseAsync("audit");
```

Do not create raw `PostgreSqlBuilder` instances. The CI architecture check enforces this.

The `Test` environment skips `MigrateAsync` in `Program.cs`; the integration test fixture controls schema creation directly to allow test isolation.
