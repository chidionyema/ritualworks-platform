# CDC Service — End-to-End Spec

**Status:** spec — not yet implemented.

**Mode:** new service (introduces `cdc-svc`) + cross-cutting changes to every existing service. The wave will run mostly as `WAVE_MODE=modify` with a small new-service component.

## 1. Goal & non-goals

### Goal

A platform-wide change-data-capture pipeline that lets any component react to data changes without coupling to source-service code or schema. Producers write to an outbox in the same transaction as their state change. A `cdc-svc` relays outbox rows to RabbitMQ as standardised `EntityChangedEvent` messages. Consumers — cache, search, audit, analytics, webhooks, anything future — subscribe by entity type, no shared types between source and destination.

The architectural property: **adding a new consumer never requires changes to producers; adding a new producer never requires changes to consumers**. Both sides talk to the same generic envelope.

### Non-goals

- True DB-level CDC via Postgres logical replication / Debezium / Kafka. Heavier operationally and adds Kafka. **Outbox is sufficient for the listed use cases.** True CDC is a separate spec if a use case ever requires it (capturing changes from external migrations, black-box services, etc.).
- Streaming SQL / data-warehouse synchronisation framework. Analytics consumes the same `EntityChangedEvent` stream and ships to a warehouse — but the warehouse design is its own spec.
- Replacing existing domain events. They keep carrying business intent. CDC carries data history. **Both patterns coexist.**

## 2. Architecture at a glance

```
┌──────────────────────────────────────────────────────────────────────┐
│  PRODUCERS (every service that mutates state)                       │
│  ┌────────────────────┐                                             │
│  │ Catalog            │   ← writes products + WRITES OUTBOX in same │
│  │   products tbl     │     transaction (Outbox library, ~5 lines)  │
│  │   outbox_events    │                                             │
│  └────────────────────┘                                             │
└──────────────────────────────────────────────────────────────────────┘
            │  (polling / logical replication)
            ▼
┌──────────────────────────────────────────────────────────────────────┐
│  cdc-svc (new microservice)                                         │
│  - polls every service's outbox_events table on a tight loop       │
│  - publishes one EntityChangedEvent per row to RabbitMQ            │
│  - marks outbox row as published; deletes after retention window   │
│  - exposes admin API for backfill / replay / pause                 │
│  - emits per-service / per-entity-type metrics                     │
└──────────────────────────────────────────────────────────────────────┘
            │  RabbitMQ topic exchange "cdc.entity.<entity_type>"
            ▼
┌──────────────────────────────────────────────────────────────────────┐
│  CONSUMERS (subscribe by entity_type filter, no shared types)      │
│  ┌──────────────┐ ┌──────────────┐ ┌──────────────┐ ┌────────────┐ │
│  │ cache-svc    │ │ search-svc   │ │ audit-svc    │ │ analytics  │ │
│  │ invalidation │ │ reindex      │ │ data history │ │ → warehouse│ │
│  └──────────────┘ └──────────────┘ └──────────────┘ └────────────┘ │
│  ┌──────────────┐                                                   │
│  │ webhooks-svc │                                                   │
│  │ subscribers  │                                                   │
│  └──────────────┘                                                   │
└──────────────────────────────────────────────────────────────────────┘
```

## 3. The wire contract — `EntityChangedEvent`

The **only** thing that crosses service boundaries. Producers and consumers each implement against this envelope; nothing else couples them.

```json
{
  "event_id": "uuid",
  "entity_type": "product",
  "entity_id": "uuid-or-string",
  "change_type": "created" | "updated" | "deleted",
  "occurred_at": "2026-05-10T...",
  "source_service": "catalog",
  "source_transaction_id": "uuid",
  "schema_version": 1,
  "payload_before": null | {...},
  "payload_after": null | {...},
  "metadata": {
    "correlation_id": "...",
    "actor_id": "...",
    "actor_type": "user" | "system",
    "trigger": "api" | "background" | "migration"
  }
}
```

**Schema discipline:**
- `entity_type` is a string. Producers and consumers agree by convention, not by C# type.
- `payload_before` / `payload_after` are jsonb. The producer chooses what to include — typically the full current entity row, or a stable projection. **No nested foreign objects.**
- `schema_version` is bumped on breaking payload shape changes; consumers tolerate `schema_version` <= max-known.
- Field additions are non-breaking. Field removals or renames bump `schema_version` and require coordinated consumer rollout.

**Routing:** RabbitMQ topic exchange `cdc.entity` with routing key `<entity_type>.<change_type>`. Consumers bind queues to whatever subset of routing keys they care about.

## 4. Producer side — the Outbox pattern

### 4.1 Schema (added to every producing service's DB)

```sql
CREATE TABLE outbox_events (
    id              uuid         PRIMARY KEY DEFAULT gen_random_uuid(),
    entity_type     text         NOT NULL,
    entity_id       text         NOT NULL,
    change_type     text         NOT NULL CHECK (change_type IN ('created','updated','deleted')),
    payload_before  jsonb,
    payload_after   jsonb,
    occurred_at     timestamptz  NOT NULL DEFAULT now(),
    metadata        jsonb        NOT NULL DEFAULT '{}',
    schema_version  int          NOT NULL DEFAULT 1,
    published_at    timestamptz,                              -- null until cdc-svc relays
    published_attempts int       NOT NULL DEFAULT 0
);

CREATE INDEX outbox_events_pending ON outbox_events (occurred_at)
    WHERE published_at IS NULL;
```

Index covers the relay's hot query (oldest unpublished). The retention worker deletes rows where `published_at < now() - interval '7 days'`.

### 4.2 Library (`Haworks.BuildingBlocks.Cdc`)

A new BuildingBlocks namespace. Producer integration is one DI registration + one call per state change:

```csharp
// Program.cs (one line, all services)
builder.Services.AddOutbox<CatalogDbContext>();

// In a command handler — write outbox in the same transaction
public async Task Handle(UpdateProductCommand cmd) {
    var product = await _db.Products.FindAsync(cmd.Id);
    var before  = product.Snapshot();
    product.Apply(cmd);
    await _outbox.RecordAsync("product", product.Id, "updated", before, product.Snapshot());
    await _db.SaveChangesAsync();           // both INSERT + outbox INSERT in one tx
}
```

Library responsibilities:
- Provides `IOutboxWriter` that buffers within the current `DbContext`
- Auto-flushes on `SaveChangesAsync` so all writes commit atomically
- Strict invariants: no I/O during `RecordAsync` (no MQ publish, no HTTP) — only the relay does that asynchronously
- `Snapshot()` extension on entities returns a jsonb-friendly dictionary (DDD aggregate state)

### 4.3 What about commands that produce domain events?

Existing pattern (kept):

```csharp
await _eventBus.PublishAsync(new ProductPriceChangedEvent { ... });
```

The domain event carries **business intent** — "price changed for promotional reason". The outbox row carries **data state** — "products row updated, before=X after=Y". Both go in the same transaction. Consumers choose:
- Need business meaning (e.g. compute promo win-rate) → subscribe to `ProductPriceChangedEvent`
- Need data sync (e.g. reindex search) → subscribe to `EntityChangedEvent { entity_type: "product" }`

This is the coexistence rule.

## 5. The cdc-svc — relay + admin

### 5.1 Relay loop

For each registered producer service:
1. Open a connection to the producer's Postgres (read-only role with `SELECT` on `outbox_events`).
2. Poll `SELECT * FROM outbox_events WHERE published_at IS NULL ORDER BY occurred_at LIMIT 100` every 500ms.
3. For each row: publish `EntityChangedEvent` to RabbitMQ exchange `cdc.entity`, routing key `<entity_type>.<change_type>`.
4. UPDATE `published_at = now()`, increment `published_attempts`.
5. On RabbitMQ publish failure: log, don't update `published_at`, retry on next poll.

**At-least-once guarantee:** if cdc-svc crashes after publishing but before UPDATE, the row gets republished on next poll. Consumers must be idempotent (use `event_id`).

**Strict ordering per `(entity_type, entity_id)`:** the LIMIT-by-occurred_at + per-key processing in the consumer guarantees this. Cross-entity ordering is not preserved.

### 5.2 Configuration

`cdc-svc` reads from a `cdc_sources` table (in cdc-svc's own DB):

```sql
CREATE TABLE cdc_sources (
    service_name    text PRIMARY KEY,
    connection      text NOT NULL,           -- read-only Postgres connection
    enabled         bool NOT NULL DEFAULT true,
    poll_interval_ms int NOT NULL DEFAULT 500,
    batch_size      int NOT NULL DEFAULT 100
);
```

Adding a new producer service = INSERT one row. No code change in cdc-svc.

### 5.3 Admin API

```
GET    /cdc/status                            # overview
GET    /cdc/sources                           # list of producers
POST   /cdc/sources/{name}/pause              # stop relaying
POST   /cdc/sources/{name}/resume
GET    /cdc/lag                               # per-source: oldest unpublished row age
POST   /cdc/replay                            # body: {entity_type, since: timestamptz}
POST   /cdc/backfill                          # body: {entity_type, source_query}  ← see § 9
```

## 6. Consumer side — clean adapters per use case

The consumer-side recipe: subscribe to `cdc.entity` exchange with a routing-key filter, dispatch through `ICdcEventHandler`.

```csharp
public interface ICdcEventHandler {
    Task HandleAsync(EntityChangedEvent change, CancellationToken ct);
}
```

Each consumer implements this once and provides routing config (which entity types they care about). **No consumer ever imports another service's types.**

### 6.1 Cache invalidation (`cache-invalidation` — new component, lives inside whoever owns the cache today, e.g. BffWeb or per-service)

Config-driven (not hard-coded per type):

```yaml
# infra/apps/cache-invalidator/config/rules.yaml
rules:
  - entity_type: "product"
    invalidate_keys:
      - "catalog:product:{entity_id}"
      - "search:product:{entity_id}"
      - "bff:home:featured"          # invalidate dependent rollups too
  - entity_type: "category"
    invalidate_keys:
      - "catalog:category:{entity_id}"
      - "catalog:category:tree"
```

Handler reads the rule for `change.entity_type`, formats the keys with `change.entity_id`, calls Redis `DEL`. Adding a new entity type to invalidate = config edit, no code change.

### 6.2 Search indexing (`search-svc` — adapt existing)

Already designed in `search-decoupling-spec.md`. The `search_index_event_registrations` table now points to `cdc.entity` events instead of typed `Haworks.Contracts.Catalog.*Event`. The generic `IndexableEntityChangedConsumer<EntityChangedEvent>` becomes the single consumer.

### 6.3 Audit (`audit-svc` — adapt existing)

Two parallel modes:
- **Business audit** (existing): consumes `IDomainEvent` via the redaction + extractor pipeline. Carries semantic meaning. Stays as-is.
- **Data audit** (new): consumes `EntityChangedEvent`. Stores raw payload-before/after pairs in a separate `data_audit_events` table for compliance / debug ("what was this row at 2pm yesterday?"). Different table, different SLA.

The two modes don't interfere; consumers can opt in to either or both.

### 6.4 Analytics (`analytics-svc` — new service, deferred per roadmap)

The simplest CDC consumer: every `EntityChangedEvent` ships to a warehouse table partitioned by entity_type. No transformation in-flight; warehouse-side jobs do the modelling.

```csharp
public class CdcToWarehouseHandler : ICdcEventHandler {
    public async Task HandleAsync(EntityChangedEvent c, CT ct) =>
        await _warehouse.AppendAsync($"raw_{c.entity_type}", c, ct);
}
```

No per-entity-type code. Adding a new entity = a new warehouse table.

### 6.5 Webhooks (`webhooks-svc` — new service, deferred per roadmap)

Subscribers register interest with filters: `(entity_type, change_type)`. Handler matches subscribers, dispatches HTTP POST per subscriber.

```csharp
public class WebhookDispatcher : ICdcEventHandler {
    public async Task HandleAsync(EntityChangedEvent c, CT ct) {
        var subs = await _subs.MatchAsync(c.entity_type, c.change_type);
        await Task.WhenAll(subs.Select(s => _http.PostJsonAsync(s.url, c, ct)));
    }
}
```

Subscriber registration is the public API of webhooks-svc. CDC is the firehose under it.

## 7. How the existing implementations adapt

This is the migration plan. The phrase "minimal touch" is the discipline — every change kept tight to avoid a months-long refactor.

### 7.1 Producers (every service with state)

| Service | Touch | Effort |
|---|---|---|
| **Catalog** | + `outbox_events` migration; integrate `IOutboxWriter` in command handlers (5 commands typical); ~10 line diff per handler | 2h |
| **Orders** | Same pattern. Order state changes already produce domain events; adding outbox is a sibling write. | 2h |
| **Payments** | Same. Payment intents + transitions + refunds = ~6 outbox writes. | 2h |
| **Identity** | Same. User profile changes are the main outbox source. | 1h |
| **Content** | Same. File-uploaded / file-deleted are the main events. | 1h |
| **Notifications** | Likely OPTIONAL. Notifications are pull-based (consumer of intent), not the source of much state that other services care about. Skip unless a real consumer emerges. | 0-1h |
| **Search** | Search-svc doesn't own state others react to (it owns its index). Skip producer integration. | 0h |
| **Audit** | Audit doesn't write business state. Skip. | 0h |
| **CheckoutOrchestrator** | Saga state transitions could go to outbox if other services want to react. Defer until needed. | 0-2h |
| **BffWeb** | No state of its own. Skip. | 0h |

Total producer integration: ~8 hours of mechanical work, **easily parallelizable** (one track per service).

### 7.2 Existing consumers — what changes

| Consumer | Today | After CDC |
|---|---|---|
| **search-svc** `CategoryUpdatedConsumer`, `ProductCacheInvalidatedConsumer` | typed `IConsumer<T>` for specific Catalog events | replaced by generic `IndexableEntityChangedConsumer` consuming `cdc.entity.product.*` and `cdc.entity.category.*`. Already covered by `search-decoupling-spec.md` — just point its registrations at CDC instead of business events. |
| **audit-svc** | `AuditConsumer<T>` consumes `IDomainEvent`. Stores semantic events. | unchanged for business audit. New parallel `DataAuditConsumer` consumes `EntityChangedEvent` to populate `data_audit_events`. |
| **catalog cache** invalidation (today scattered in handlers) | Manual `cache.RemoveAsync(key)` calls | move to the dedicated cache-invalidation consumer (§ 6.1). Remove the manual calls from handlers. |
| **bff cache** | Probably similar manual invalidation | same — move to config-driven CDC consumer. |
| **MassTransit consumers in general** | `IConsumer<SpecificEvent>` for everything | unaffected for business event consumers. CDC adds a parallel channel; doesn't replace it. |

### 7.3 Domain events that become candidates for retirement

Once CDC is in place, several existing domain events do "data sync" work that CDC does better:
- `ProductCacheInvalidatedEvent` — was specifically for cache invalidation. Replaced by CDC + cache-invalidation consumer. **Can be retired** after consumers move.
- `CategoryUpdatedEvent` (search-only consumer) — replaced by CDC. Retire.
- `StockReleasedEvent` / `StockReservedEvent` — these carry business semantics (saga state) so they STAY.
- `OrderCreatedEvent` etc. — business events, stay.

The retirement is **gradual**: don't drop domain events until every consumer has moved off them. Coexistence is fine.

## 8. Monitoring CDC in production

The unique observability needs of CDC: lag, throughput, error rates, end-to-end latency.

### 8.1 Metrics emitted

All under `cdc.*` prefix, scraped by Prometheus (matches existing OTLP pipeline).

| Metric | Type | Tags | What it tells you |
|---|---|---|---|
| `cdc.outbox.depth` | gauge | `service` | unrelayed rows per source — should hover near zero |
| `cdc.outbox.oldest_age_seconds` | gauge | `service` | age of the oldest unrelayed row — alarm if > 60s |
| `cdc.outbox.publish.rate` | counter | `service`, `entity_type` | events published per second |
| `cdc.relay.publish.duration_seconds` | histogram | `service` | DB read → MQ publish latency |
| `cdc.relay.publish.failures` | counter | `service`, `reason` | publish failures (RabbitMQ unreachable, etc.) |
| `cdc.consumer.lag_seconds` | gauge | `consumer`, `entity_type` | consumer offset behind producer |
| `cdc.consumer.processing.duration_seconds` | histogram | `consumer`, `entity_type` | per-event processing time |
| `cdc.consumer.failures` | counter | `consumer`, `reason` | consumer-side errors |
| `cdc.consumer.dlq.depth` | gauge | `consumer` | events parked for human review |
| `cdc.e2e.latency_seconds` | histogram | `producer→consumer` | tx commit → consumer ack |

### 8.2 Dashboards

One Grafana dashboard `CDC Overview`:
- Top: outbox depth + oldest-age per source (red if > thresholds)
- Middle: publish rate by entity_type (stacked area)
- Bottom: consumer lag + DLQ depth per consumer

Per-consumer dashboards drill into its specific behaviour.

### 8.3 Alarms

| Condition | Severity | Page |
|---|---|---|
| `cdc.outbox.oldest_age_seconds > 120` for any service for 5min | high | yes |
| `cdc.consumer.dlq.depth > 0` for any consumer | medium | no, ticket |
| `cdc.consumer.lag_seconds > 300` for 10min | medium | no, ticket |
| `cdc.relay.publish.failures` rate > 1/s for 5min | high | yes |
| `cdc.e2e.latency_seconds` p99 > 10s for 15min | medium | ticket |

### 8.4 Tracing

Every `EntityChangedEvent` carries a `correlation_id` from the producing transaction. The relay propagates it via OpenTelemetry baggage. A trace spans: HTTP request → outbox INSERT → relay publish → RabbitMQ → consumer processing → ack. Drilling into Tempo shows the whole chain for any entity change.

## 9. Operating CDC in production

The day-2 operations surface — what an operator does when something looks off.

### 9.1 The `cdc` CLI

Same shape as `wave` and `platform` CLIs in this repo (bash with subcommands). Hits cdc-svc admin API.

```bash
cdc status                              # overview: sources + consumers + DLQ depths
cdc lag                                 # per-source/per-consumer lag in one view
cdc inspect <event_id>                  # full event + delivery history (which consumers, which acked)

# producer-side ops
cdc source pause <service>              # stop relaying from that source (e.g., during a runaway fix)
cdc source resume <service>
cdc source backfill <service> <entity_type>   # generate change events for the current state of every row in that table — used after adding a new consumer or a new outbox to a service that didn't have one
cdc retention <service> --days 14       # adjust retention for outbox rows post-publish

# consumer-side ops
cdc consumer pause <consumer-name>      # stop a specific consumer (others keep running)
cdc consumer resume <consumer-name>
cdc consumer reset <consumer-name> --since <ts>   # replay events from a timestamp (consumer must be idempotent)
cdc consumer dlq list <consumer-name>   # list failed events parked
cdc consumer dlq retry <event_id>       # re-enqueue a parked event
cdc consumer dlq drop <event_id>        # mark as resolved without retry (with reason)
```

### 9.2 Common day-2 scenarios

| Scenario | Procedure |
|---|---|
| Search index out of date | `cdc consumer reset search --since <ts>` — replays all entity changes since `<ts>` through the search consumer |
| New service added; needs to backfill historical data | `cdc source backfill catalog product` — generates events for every existing product row, dispatched as `cdc.entity.product.created` |
| Producer outbox is bloating | `cdc lag` to identify which consumer is behind; investigate that consumer; or `cdc consumer reset` to skip |
| Bad payload (e.g., NULL where consumer expected non-null) | message lands in DLQ; `cdc consumer dlq retry <id>` after fix, or `drop` if permanently broken |
| Migration deletes 1M rows; outbox would be flooded | run migration with `SET LOCAL session_replication_role = replica` to skip outbox triggers; then `cdc source backfill <service> <entity_type>` to emit fresh state events |
| Want to test a new consumer in production without it affecting anything | deploy with `?replay-only=true` flag — consumer reads but doesn't ack publicly; you compare its decisions against production |

### 9.3 Runbook companion

`docs/runbooks/cdc-operations.md` captures the above scenarios + Vault-style "in case of fire" procedures (full DLQ replay, source pause + drain, etc.). Lives next to the existing observability runbooks.

## 10. Test plan

### 10.1 Unit (fast)

- Outbox library: `RecordAsync` enrolls in current tx; rollback drops outbox row
- Schema-version evolution: handler tolerant of additive fields
- Routing: `entity_type` → exchange routing key correct

### 10.2 Integration (per consumer)

- Cache: emit `EntityChangedEvent`, assert Redis key absent; emit `created`, assert key NOT cached (cache is invalidate-only, not preload)
- Search: emit `cdc.entity.product.updated`, assert Meilisearch document mutated
- Audit (data-mode): emit event, assert `data_audit_events` row persisted
- Analytics (when built): emit event, assert warehouse row appended

### 10.3 E2E (super journey, ports the Phase 3c spec)

`CdcEndToEndJourney` — write a product via Catalog API; assert (a) outbox row, (b) cdc-svc publishes within 1s, (c) cache invalidated, (d) search index updated, (e) data-audit row, (f) analytics row, (g) any registered webhook fired. Single test, exhausts the chain.

### 10.4 Chaos

- Kill cdc-svc mid-relay; assert resumed publishing on restart with no duplicates beyond expected at-least-once
- Pause a consumer for 10 min while traffic flows; resume; assert no events lost
- Bad payload in outbox; assert it's DLQ'd, not blocking the queue

## 11. Implementation plan — parallel decomposition for one-day delivery

`wave run docs/agent-briefs/cdc-service-spec.md`. Mode: hybrid (introduces cdc-svc + modifies every existing producer + modifies search/audit consumers). The wave splits into 9 disjoint-scope tracks:

| Track | Owns | Hours |
|---|---|---|
| **T1** Outbox library + schema | `src/BuildingBlocks/Cdc/**`, migration template, unit tests | 3 |
| **T2** cdc-svc skeleton + relay loop | `src/Cdc/**` (new service via wave's new-service scaffold), relay worker, RabbitMQ publisher | 4 |
| **T3** cdc-svc admin API + CLI | controllers, `tools/cdc` CLI, runbook doc | 3 |
| **T4** Per-service producer integration (Catalog, Orders, Payments) | outbox migration + handler changes for these 3 services | 3 |
| **T5** Per-service producer integration (Identity, Content) | outbox migration + handler changes for the lighter services | 2 |
| **T6** Search consumer migration | refactor search-svc per `search-decoupling-spec.md` to consume CDC events instead of typed Catalog events | 3 |
| **T7** Audit data-audit consumer | new parallel `DataAuditConsumer` in audit-svc + `data_audit_events` table | 3 |
| **T8** Cache invalidation consumer | new component (or in BffWeb), config-driven rules YAML, replaces scattered manual invalidation | 3 |
| **T9** Monitoring + dashboards + alarms + E2E test | Prometheus rules, Grafana dashboards, the `CdcEndToEndJourney` test, the runbook | 3 |

**Disjoint-scope contract:** T4 and T5 split producers across different services (no overlap). T6 owns only `src/Search/**` consumer changes. T8 introduces a new component (no edits to existing handlers' invalidation calls — those get removed in a follow-up sweep).

**Total wall-clock with 9 agents in parallel: ~4 hours.**

After wave merges to `feat/cdc-platform`, integration smoke = the `CdcEndToEndJourney` test in T9. PR `feat/cdc-platform → main` is the rollup.

## 12. Reference projects to mirror

- `MassTransit` outbox docs — the library's design follows MassTransit's transactional outbox patterns
- `Debezium` documentation — for understanding the full CDC space (we're choosing simpler outbox-only for now)
- existing `src/BuildingBlocks/` patterns for the library shape
- existing `audit-svc` for what a CDC consumer looks like in this codebase
- `tools/wave` for the CLI shape that `tools/cdc` mirrors
