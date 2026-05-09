# Audit Service — End-to-End Spec

Captures every domain event published on RabbitMQ to an append-only,
queryable store. Provides a read API for compliance audits, support
investigations ("what happened to order #1234?"), and security forensics.

Pairs with the cross-cutting roadmap (Tier 1, build first).

## 1. Goal & non-goals

### Goal
Single source of truth for "who did what when" across all domain
services. One event in → one immutable row out. Queryable by entity id
(order_id, user_id, payment_id), event type, time range.

### Non-goals
- Not application observability — Tempo/Grafana already does that. Audit is for *business* events, not HTTP request traces.
- Not a domain replay store. We're not going to event-source aggregates from this. If a service needs to reconstruct state, it owns its own outbox.
- Not real-time analytics. Streaming aggregations belong in a warehouse (BigQuery / Snowflake / Metabase). Audit is a transactional read of recent events.
- Not an alerting source. Don't trigger PagerDuty off audit log rows; that's what Grafana alerts are for.

## 2. Architecture at a glance

```
+--------+   +---------+   +----------+   +---------+
| Orders |   | Payments|   | Catalog  |   | Identity| ... (all backends)
+---+----+   +----+----+   +----+-----+   +----+----+
    |             |             |              |
    +-------------+----+--------+--------------+
                       |  RabbitMQ (existing)
                       v
              +-------------------+
              | audit-svc         |
              |  - 1 consumer per |
              |    event type     |
              |  - writes to PG   |
              |    audit_events   |
              +---------+---------+
                        |
                        v
              +-------------------+
              | Postgres          |
              |  audit_events     |   <-- append-only, partitioned by month
              |  audit_event_idx  |
              +-------------------+
                        |
              GET /audit/...      <-- read API for support/compliance
              v
              +-------------------+
              | BffWeb (admin)    |
              +-------------------+
```

Single-process .NET service; one MassTransit consumer per published
event contract; all writes go to one Postgres table. No outbox needed
— this service has no outbound events.

## 3. Contracts

### 3.1 HTTP — request/response

All routes mounted under `/audit`. JWT-required, `audit-reader` role
gates read access; `audit-admin` gates exports.

#### `GET /audit/events`
Query params:
- `entityId` (string, optional) — filter to events about this entity (order_id, user_id, …).
- `entityType` (enum: `order|user|payment|product|cart|content|search-index`).
- `eventType` (string, optional) — full type name e.g. `OrderCompletedEvent`.
- `from` (ISO-8601, required).
- `to` (ISO-8601, required, max range = 90 days).
- `limit` (int, default 100, max 1000).
- `cursor` (opaque, for pagination).

Response:
```json
{
  "items": [
    {
      "id": "uuid",
      "occurredAt": "2026-05-09T18:00:00Z",
      "eventType": "OrderCompletedEvent",
      "entityType": "order",
      "entityId": "order-1234",
      "actorId": "user-abc",
      "actorType": "user",
      "correlationId": "corr-xyz",
      "payload": { /* original event payload, redacted of secrets */ },
      "metadata": {
        "publishedBy": "orders-svc",
        "rabbitMqRoutingKey": "Haworks.Contracts.Orders.OrderCompletedEvent",
        "messageId": "msg-uuid"
      }
    }
  ],
  "nextCursor": "opaque-base64"
}
```

#### `POST /audit/export`
Async — returns a job id, polls a CSV file in S3. For
compliance dumps. Body: same filter as `GET /audit/events`. 24-hour
signed URL.

Response:
```json
{ "jobId": "uuid", "status": "queued" }
```

#### `GET /audit/export/{jobId}`
Polling endpoint:
```json
{
  "jobId": "uuid",
  "status": "queued|running|succeeded|failed",
  "downloadUrl": "https://...signed...",     // when succeeded
  "error": "..."                             // when failed
}
```

#### `GET /audit/events/{id}`
Single-event lookup by audit id.

### 3.2 Inbound events

Subscribes to **every** contract under `src/Contracts/`. Current set
(at time of spec):

- `Catalog/{CategoryUpdatedEvent, ProductCacheInvalidatedEvent, StockReservationRequestedEvent, StockReservedEvent, StockReservationFailedEvent, StockReleasedEvent, StockReleaseRequestedEvent}`
- `Checkout/{CheckoutInitiatedEvent, PaymentExpiredEvent}`
- `Identity/{UserProfileChangedEvent, VaultRotationStageEvent}`
- `Orders/{OrderAbandonedEvent, OrderCompletedEvent, OrderCreatedEvent}`
- `Payments/{PaymentSessionRequestedEvent, PaymentSessionCreatedEvent, PaymentSessionFailedEvent, PaymentCompletedEvent, PaymentVerifiedEvent, PaymentWebhookValidatedEvent, PaymentAmountMismatchEvent, RefundIssuedEvent, SubscriptionStartedEvent, SubscriptionRenewedEvent, SubscriptionCancelledEvent, CheckoutSessionExpiredEvent}`

Auto-discovery: at startup, the service reflects over the
`Haworks.Contracts` assembly and registers a generic
`AuditConsumer<TEvent>` for every record marked `IDomainEvent`.

### 3.3 Outbound

None. This is a leaf service.

## 4. Data model — Postgres `audit_events`

```sql
CREATE TABLE audit_events (
    id              UUID         NOT NULL DEFAULT gen_random_uuid(),
    occurred_at     TIMESTAMPTZ  NOT NULL,
    received_at     TIMESTAMPTZ  NOT NULL DEFAULT now(),
    event_type      TEXT         NOT NULL,    -- e.g. 'OrderCompletedEvent'
    entity_type     TEXT         NOT NULL,    -- 'order', 'user', etc.
    entity_id       TEXT         NOT NULL,
    actor_id        TEXT,                     -- nullable: system-emitted events
    actor_type      TEXT,                     -- 'user', 'system', 'webhook'
    correlation_id  TEXT,                     -- saga / request correlation
    payload         JSONB        NOT NULL,    -- full event body, secrets stripped
    metadata        JSONB        NOT NULL,    -- routing key, source service, message_id, etc.
    PRIMARY KEY (id, occurred_at)
) PARTITION BY RANGE (occurred_at);

-- monthly partitions, created by a BackgroundService 14 days ahead
CREATE TABLE audit_events_2026_05 PARTITION OF audit_events
    FOR VALUES FROM ('2026-05-01') TO ('2026-06-01');

-- Indexes (created on each partition; keep the per-partition definitions in
-- the rollover service so new partitions inherit the same shape).
CREATE INDEX audit_events_entity_idx
    ON audit_events (entity_type, entity_id, occurred_at DESC);
CREATE INDEX audit_events_correlation_idx
    ON audit_events (correlation_id) WHERE correlation_id IS NOT NULL;
CREATE INDEX audit_events_event_type_idx
    ON audit_events (event_type, occurred_at DESC);

-- Idempotency on message_id (per-partition partial unique index — partition
-- detach must remain instant, so no global unique).
CREATE UNIQUE INDEX audit_events_msg_id_uniq_2026_05
    ON audit_events_2026_05 ((metadata->>'message_id'))
    WHERE metadata->>'message_id' IS NOT NULL;
```

Partition retention: 13 months hot in Postgres, then detached + moved
to S3 (CSV) for cold-storage compliance access. Detach is an
`ALTER TABLE … DETACH PARTITION` — instant, no rewrite.

## 5. Pipeline

### 5.1 Event extraction

Each consumer pulls these fields out of the event payload using a
small `IAuditExtractor<T>` per type:

```csharp
public interface IAuditExtractor<T> where T : IDomainEvent
{
    AuditRow Extract(T evt, ConsumeContext ctx);
}

public sealed record AuditRow(
    DateTimeOffset OccurredAt,
    string EventType,
    string EntityType,
    string EntityId,
    string? ActorId,
    string? ActorType,
    string? CorrelationId,
    JsonElement Payload,
    JsonElement Metadata);
```

`ReflectionAuditExtractor<T>` is the default: looks for
`OrderId`/`UserId`/`PaymentId`/`SkuId`/`ProductId`/`CartId` properties
in that order; first match wins; the property name (lowercased, minus
the `Id` suffix) becomes `entity_type`. Falls back to
`entity_type="unknown", entity_id=""` rather than throwing — `unknown`
rows are still queryable by event_type + occurred_at.

A hand-written extractor overrides for events where the mapping isn't
obvious. At minimum:
- `StockReservationFailedEvent` carries both `SkuId` and `OrderId` — pick `OrderId` (the support question is "what happened to order X", not "what happened to sku Y").
- `VaultRotationStageEvent` is operational, not user-facing — `entity_type="system", entity_id=ServiceName`.
- `ProductCacheInvalidatedEvent` ditto — `entity_type="cache", entity_id=CacheKey`.

### 5.2 Redaction

Before insert, run the payload through a `SecretRedactor` that strips:
- Anything in a property whose name (case-insensitive) ends in
  `token`, `password`, `secret`, `key`, `credential`, `apikey`, or
  `authorization`.
- Stripe / PayPal raw webhook bodies (kept as a hash + signature only — strip the `RawBody` property if present, replace with `RawBodySha256`).
- Credit-card numbers (regex `\b(?:\d[ -]*?){13,19}\b` validated by Luhn) → replace with `****<last4>`.
- 3-or-4-digit CVVs in fields named `cvv`/`cvc`/`securityCode` → drop entirely.

Redaction is **deny-list** now, regex/property-name based. Move to allow-list (`PII fields are explicitly tagged on the event contract`) when contracts are stable. Add a metric `audit.redaction.fields_stripped_total{field_pattern}` so we see what's being scrubbed in practice.

### 5.3 Idempotency

MassTransit gives at-least-once delivery. Use the event's `MessageId`
from `ConsumeContext.MessageId` as a unique key on
`audit_events.metadata->>'message_id'`; conflict = silent skip. The
unique constraint is **per-partition partial unique** (see schema
above) — keeps partition detach instant.

If `MessageId` is null (some legacy events), generate a deterministic
hash of (event_type + payload + occurred_at) and use that.

### 5.4 Throughput

Target: 10k events/sec sustained, 50k peak. Achievable with:
- 4 consumer concurrency per event type (tune via MassTransit `PrefetchCount`).
- Postgres `COPY` batching every 50 events / 200ms (whichever first).
- `audit_events` table on dedicated tablespace if disk contention bites.
- `IAuditWriter` interface so the COPY-batched implementation can be swapped for a per-row insert in tests.

## 6. Other-service changes required

**Minimal.** This is a parasitic listener — no producer changes.

The one optional addition: introduce a top-level `IDomainEvent`
marker (already exists at `src/Contracts/IDomainEvent.cs`) on every
contract. Most are already marked. Verify:

```bash
grep -L "IDomainEvent" src/Contracts/*/*.cs
```

For any that don't implement it, add the marker. No behaviour change
for anyone else.

Also add `correlation_id` propagation in MassTransit's
`SendEndpointProvider` if it isn't already (check
`src/BuildingBlocks/`); audit's correlation queries depend on it.

## 7. SLA targets

- **Capture latency** (event published → row visible): p50 < 200ms, p95 < 1s, p99 < 5s.
- **Read p95** (`GET /audit/events` with single-entity filter, 24-hour range): < 100ms.
- **Read p95** (90-day range, no entity filter): < 2s (using event_type + occurred_at index).
- **Loss budget**: zero. If the consumer can't keep up, RabbitMQ queue must back up — never drop. Alert on queue depth > 10k.
- **Availability**: 99.5% (this is reporting, not transactional).

## 8. Topology & deployment

- **Aspire**: `var auditDb = postgres.AddDatabase("audit");` then `var audit = builder.AddProject<Projects.Audit_Api>("audit-svc").WaitFor(rabbitmq).WithReference(auditDb).WithReference(rabbitmq)` plus `AddJwksConfig(audit, identity)` and `.WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", tempo.GetEndpoint("grpc"))` per the existing pattern.
- **Compose**: standard backend service pattern (mirror `notifications-svc` in `deploy/compose/docker-compose.yml`). Dockerfile at `src/Audit/Audit.Api/Dockerfile`.
- **Fly.io**: `fly.audit.toml` mirroring `fly.orders.toml`. 1 machine per region; vertical-scale only — this service is bursty during product peaks. Static OTel env (matching the other 8) plus `fly secrets set OTEL_EXPORTER_OTLP_ENDPOINT=...` operator step.
- **Resource sizing**: 256MB RAM, 0.25 vCPU baseline. JSON parsing dominates CPU.
- **Postgres init**: add `audit` to the `CREATE DATABASE` iterator in `deploy/aspire/init-postgres.sql`.

## 9. Test plan

### 9.1 Unit (`tests/Audit.Unit/`)
- `ReflectionAuditExtractorTests` — golden inputs per representative event type → expected `AuditRow`.
- `HandWrittenExtractorTests` — overrides for `StockReservationFailedEvent`, `VaultRotationStageEvent`, `ProductCacheInvalidatedEvent`.
- `SecretRedactorTests` — every redaction rule + a fuzzer with random JSON to ensure nothing leaks.
- `AuditQueryBuilderTests` — filter combinations → expected SQL fragments.

### 9.2 Integration (`tests/Audit.Integration/`)
- `EndToEndCaptureTests` — publish each contract type to RabbitMQ via Testcontainers, assert exactly one `audit_events` row appears, payload matches input minus redacted fields.
- `IdempotencyTests` — publish the same `MessageId` twice; exactly one row.
- `PartitionRolloverTests` — manipulate `TimeProvider`, ensure new partition is auto-created.
- `QueryApiTests` — full HTTP coverage of all filter combinations against a seeded fixture.
- `ExportJobTests` — produce a small range, assert CSV content matches DB query.
- Use `SharedTestPostgres` per the existing pattern; LocalStack S3 for export tests.

### 9.3 Performance (`tests/Audit.Perf/`) — out of scope for this build, mark as TODO.

### 9.4 Smoke (`tests/Smoke/`)
- `AuditSmokeTests` — exercise an order checkout end-to-end against the live stack, then assert `OrderCreated`, `PaymentCompleted`, `OrderCompleted` rows exist within 5s.

## 10. Observability

- Trace every consumer with the existing OTEL setup; span name `audit.consume.<EventType>`.
- Metric: `audit.events.captured_total{event_type, source_service}`.
- Metric: `audit.events.lag_seconds` (now − occurred_at, sampled 1%).
- Metric: `audit.redaction.fields_stripped_total{field_pattern}`.
- Log line at WARN if any consumer's queue depth > 5k.
- Dashboard: panel per event_type, showing rate + lag p95.

## 11. Failure modes & runbook stubs

| Failure                                          | Detection                                      | Mitigation                                                                                              |
| ------------------------------------------------ | ---------------------------------------------- | ------------------------------------------------------------------------------------------------------- |
| Postgres write outage                            | RabbitMQ queue depth alert                     | Service auto-pauses consumer; drain queue when DB recovers. RabbitMQ TTL must be > expected outage SLA. |
| Schema drift in an event payload                 | Consumer raises + DLQs the message             | Hand-roll a migration in the consumer's mapper; replay the DLQ.                                         |
| Partition not pre-created                        | Insert fails on next-month event               | Cron lag alert; manually create partition. Cron has 14-day lookahead, so this is rare.                  |
| Redactor leaks a secret into payload             | Audit-of-audit grep job runs nightly           | Add field pattern to redactor; backfill-redact existing rows in affected partition.                     |
| Read API DDOS by curious admin                   | Rate-limit triggered                           | Per-token rate limit at gateway; expensive queries (`>30 day range`) require explicit `force=true`.     |

Each row above gets its own `docs/runbooks/audit-{slug}.md` once the
first incident teaches us what to actually do.

## 12. Implementation plan (parallel agents)

Five workstreams; the first is on the critical path, the rest can run
in parallel after L0.

- **L0 — skeleton + DI** (~2h): csproj, Aspire wiring, Postgres EF Core context, partition migration script, MassTransit registration helper. Empty consumers. Compiles. Boots in Aspire.
- **L1.A — extractors + redactor** (~4h): `IAuditExtractor<T>` + per-event implementations + reflection fallback. `SecretRedactor` with property-name + regex rules. Unit tests.
- **L1.B — capture pipeline** (~4h): generic `AuditConsumer<T>` that calls extractor + redactor + writes via `COPY` batched against `IAuditWriter`. Integration test: publish one event → row appears.
- **L1.C — query API** (~3h): `GET /audit/events` + `GET /audit/events/{id}`. `audit-reader` role on JWKS. Cursor pagination (last-seen `(occurred_at, id)` tuple base64-encoded). Integration tests against seeded data.
- **L1.D — export job + partition cron** (~3h): `POST /audit/export` writes CSV to S3 via the existing Storage abstraction; `BackgroundService` creates next-month partition 14 days ahead. Integration tests: golden CSV.
- **L2.E — perf hardening** (out of this build's scope): batch size tuning, `dotnet-counters` baseline, query-plan analysis on 50M rows. Smoke test in Aspire stack.

Total in-scope: ~16 person-hours, 1–2 calendar days for one focused agent.
