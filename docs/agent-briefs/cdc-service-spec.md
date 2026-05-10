# CDC Service — End-to-End Spec

**Status:** spec — not yet implemented.

**Mode:** new service (introduces `cdc-svc`) + DB-level configuration on every existing service (NOT application code changes — see § 4). The wave will run mostly as `WAVE_MODE=modify` with a small new-service component.

## 1. Goal & non-goals

### Goal

A platform-wide change-data-capture pipeline that lets any component react to data changes **without source services knowing they're being captured**. The capture point is the database itself, via Postgres logical replication — every committed change to a tracked table flows through Postgres' WAL stream into `cdc-svc`, which transforms it into a standardised `EntityChangedEvent` message on RabbitMQ. Consumers — cache, search, audit, analytics, webhooks, anything future — subscribe by entity type, no shared types between source and destination, no application coupling.

The architectural property: **adding a new consumer never requires changes to producers; adding a new producer is "enable logical replication on its DB" — zero application code change, zero handler edits, zero risk of forgotten events**. Migrations, manual SQL, and out-of-band edits are all captured because the DB is the source of truth.

### Non-goals

- **Application-level outbox / dual-write patterns.** Considered and rejected — they require the application to remember to record every state change, exactly the maintenance tax CDC is supposed to eliminate. Logical replication is the answer.
- **Kafka / Debezium.** A perfectly valid heavier-weight option, but this stack already has RabbitMQ. `cdc-svc` will consume the WAL stream directly (using `pgoutput` or `wal2json`) and publish to RabbitMQ — Debezium's job, simpler topology.
- **Streaming SQL / data-warehouse sync framework.** Analytics consumes the same `EntityChangedEvent` stream and ships to a warehouse — but the warehouse design is its own spec.
- **Replacing existing domain events.** They keep carrying business intent ("payment refunded for fraud reason"). CDC carries data history ("orders row, status: paid → refunded"). **Both patterns coexist.**

## 2. Architecture at a glance

```
┌──────────────────────────────────────────────────────────────────────┐
│  PRODUCERS (every service that mutates state)                       │
│  ┌────────────────────┐                                             │
│  │ Catalog            │   ← just writes to its tables as normal —   │
│  │   products tbl     │     ZERO application code change            │
│  │   …                │     Postgres has wal_level=logical and a    │
│  └────────────────────┘     publication for the tables we capture   │
└──────────────────────────────────────────────────────────────────────┘
            │  Postgres logical replication slot (WAL stream)
            ▼
┌──────────────────────────────────────────────────────────────────────┐
│  cdc-svc (new microservice)                                         │
│  - one logical replication subscriber per source DB                │
│  - decodes WAL changes (pgoutput/wal2json) → EntityChangedEvent    │
│  - publishes to RabbitMQ topic exchange "cdc.entity"               │
│  - tracks LSN per slot (replication offset); ack only after        │
│    successful publish for at-least-once delivery                   │
│  - exposes admin API for replay / pause / backfill                 │
│  - emits per-source / per-entity-type metrics                      │
└──────────────────────────────────────────────────────────────────────┘
            │  RabbitMQ topic exchange "cdc.entity"
            │  routing key = "<entity_type>.<change_type>"
            ▼
┌──────────────────────────────────────────────────────────────────────┐
│  CONSUMERS (subscribe by routing-key filter, no shared types)      │
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

**The capture is at the database layer, not in application code.** Every committed transaction's WAL records flow through cdc-svc; the application never knows. Migrations, scripted updates, even `psql` sessions are all captured. This is the property that distinguishes real CDC from manual outbox dual-write.

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

## 4. Producer side — Postgres logical replication (zero application code)

The application is unaware of CDC. Producers just write to their tables. The DB does the work.

### 4.1 Per-DB configuration (one-time, IaC)

Each service's Postgres needs three things, none of which involve application code:

**(a)** `wal_level=logical` in `postgresql.conf`. Enables WAL-based change streaming. Reload required (no downtime). Cost: ~5-10% more WAL volume on write-heavy tables, fully manageable.

**(b)** A `PUBLICATION` declaring which tables we capture:

```sql
-- Run once per service DB. Idempotent.
CREATE PUBLICATION cdc_publication FOR TABLE
    products,
    product_categories,
    product_prices
WITH (publish = 'insert, update, delete');
```

The list is per-service. Tables NOT in the publication are invisible to CDC (use this to exclude noisy or sensitive tables). The publication can be altered later via `ALTER PUBLICATION ADD TABLE` — no app restart.

**(c)** `REPLICA IDENTITY FULL` on tables that need before-image:

```sql
ALTER TABLE products REPLICA IDENTITY FULL;
```

Without this, Postgres only writes the primary key on UPDATE/DELETE — consumers wouldn't get `payload_before`. With it, the entire pre-image lands in WAL. Cost: more WAL on UPDATE/DELETE; tolerable for low-write tables, evaluate per-table for hot ones.

**Operationally:** `infra/stateful/postgres-clusters/<service>.yaml` (the CloudNativePG Cluster CR per the K8s platform spec) declares `wal_level=logical` in its config. Each service's L0 wave run produces a publication script in `infra/stateful/cdc-publications/<service>.sql` that ships alongside the service migration.

### 4.2 Application code change required

**None.** Services write to tables as they always have. No new DI registration, no library to call, no outbox table, no transaction-scoping concern. The change is purely operational/infrastructural.

### 4.3 Coexistence with domain events

Existing domain events stay. They carry **business intent** — "ProductPriceChangedEvent fired because of a promo rule" — which logical replication cannot infer. CDC carries **data history** — "products row updated, before=X after=Y". Both flow:
- Domain events → MassTransit → existing typed consumers (Notifications, Orders sagas, business handlers)
- CDC events → cdc-svc → generic consumers (cache, search, audit-data-mode, analytics, webhooks)

A consumer chooses based on what it needs. Most cross-cutting consumers want CDC (data sync); most business-flow consumers want domain events (semantic meaning).

### 4.4 What about logical replication's edge cases?

| Concern | Mitigation |
|---|---|
| **Schema changes** (DDL — ALTER TABLE etc.) | Logical replication skips DDL (Postgres limitation). cdc-svc surfaces a warning when WAL contains DDL records; ops runbook covers the resync workflow. New columns in published tables auto-flow with no consumer change required (additive). |
| **Replication slot fills disk** if cdc-svc lags | Per-slot `max_slot_wal_keep_size` on Postgres caps the slot. If cdc-svc falls too far behind, the slot is invalidated; cdc-svc detects this and surfaces a hard alarm. Operator runs `cdc source resync` — see § 9. |
| **TOAST'd columns** (large jsonb/text) only stream if changed | We're already storing jsonb in payload — REPLICA IDENTITY FULL ensures the full row, including unchanged TOAST, lands on UPDATE. |
| **Cascading deletes** generate per-row events | Expected behaviour. Consumers handle high-fan-out. |
| **Multi-statement transactions** | Logical replication preserves transaction boundaries. cdc-svc emits all changes from a transaction with the same `source_transaction_id` so consumers can correlate. |
| **Cross-table consistency** (e.g., Order + OrderLines change atomically) | Both rows' events land in the same transaction-id. Consumers needing atomic view can buffer by `source_transaction_id` until they see a `transaction_commit` marker (cdc-svc emits this). |

## 5. The cdc-svc — relay + admin

### 5.1 Relay loop (per source DB)

For each registered producer service, cdc-svc holds one logical replication slot:

1. **Subscribe** via `START_REPLICATION SLOT <slot_name> LOGICAL <last_lsn> (proto_version '4', publication_names 'cdc_publication')`. Postgres begins streaming WAL records from `<last_lsn>`.
2. **Decode** each WAL record using `pgoutput` (built-in) or `wal2json` (more JSON-friendly). For each row change: extract `table_name`, `change_type` (insert/update/delete), `payload_before`, `payload_after`, `source_transaction_id` (XID).
3. **Map** `table_name` → `entity_type` via cdc-svc config (e.g. `products` → `product`, `product_prices` → `product_price`). Tables not mapped are skipped.
4. **Publish** `EntityChangedEvent` to RabbitMQ exchange `cdc.entity`, routing key `<entity_type>.<change_type>`.
5. **Confirm and advance LSN** only after RabbitMQ confirms the publish: `pg_replication_slot_advance(<slot_name>, <published_lsn>)`. This is the "ack" that lets Postgres reclaim WAL.

**At-least-once guarantee:** if cdc-svc crashes after publishing but before LSN advance, the WAL record is replayed on reconnect. Consumers MUST be idempotent — use `event_id` (a deterministic hash of `slot_name + lsn`) for dedup.

**Ordering:** logical replication delivers WAL in commit order. Per-table ordering is preserved naturally; per-`(entity_type, entity_id)` is preserved because they all land in the same WAL stream.

**Backpressure:** if RabbitMQ is slow, cdc-svc holds onto the WAL slot but doesn't ack. Postgres retains WAL up to `max_slot_wal_keep_size`. If cdc-svc falls too far behind, the slot is invalidated and ops needs to resync — see § 9.

### 5.2 Configuration

`cdc-svc`'s own DB has two tables:

```sql
-- One row per source DB to subscribe to
CREATE TABLE cdc_sources (
    service_name      text PRIMARY KEY,
    connection_string text NOT NULL,       -- replication-role Postgres connection
    publication_name  text NOT NULL DEFAULT 'cdc_publication',
    slot_name         text NOT NULL,       -- unique per cdc-svc replica
    enabled           bool NOT NULL DEFAULT true,
    started_at        timestamptz
);

-- Mapping from table_name → entity_type (per source)
CREATE TABLE cdc_table_map (
    service_name  text NOT NULL,
    table_name    text NOT NULL,
    entity_type   text NOT NULL,
    enabled       bool NOT NULL DEFAULT true,
    PRIMARY KEY (service_name, table_name)
);
```

Adding a new producer service:
1. Run the publication script on its DB: `CREATE PUBLICATION cdc_publication FOR TABLE …`
2. INSERT into `cdc_sources` + per-table rows in `cdc_table_map`.

That's it. No code change in cdc-svc, no application code change in the producer.

### 5.3 Admin API

```
GET    /cdc/status                            # overview: each source's slot state + lag
GET    /cdc/sources                           # list of subscribed sources
POST   /cdc/sources/{name}/pause              # stop reading the slot (slot stays — WAL accumulates)
POST   /cdc/sources/{name}/resume
POST   /cdc/sources/{name}/resync             # drop + recreate the slot from a snapshot — see § 9
GET    /cdc/lag                               # per-source: LSN behind primary, age of last published change
POST   /cdc/replay                            # body: {source, since_lsn} — re-publish from LSN forward
POST   /cdc/backfill                          # body: {source, table, source_query} — see § 9
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

### 7.1 Producers (every service with state) — pure infrastructure work, no application code

This is where the design pays off — there's almost nothing to do per service:

| Service | Touch | Effort |
|---|---|---|
| **Catalog** | (a) ALTER `wal_level=logical` on its Postgres (one-time, cluster CR change); (b) `CREATE PUBLICATION cdc_publication FOR TABLE products, product_categories, product_prices`; (c) `REPLICA IDENTITY FULL` on those tables. **Zero application code change.** | 30min |
| **Orders** | Same: enable replication, declare publication for `orders`, `order_lines`. | 30min |
| **Payments** | Same: declare publication for `payments`, `payment_attempts`, `refunds`. | 30min |
| **Identity** | Same: `users`, `user_profiles`. | 30min |
| **Content** | Same: `content_items`. | 30min |
| **Notifications** | Likely SKIP. Notifications are pull-driven (consumers of intent); little state others react to. Re-evaluate if a real consumer emerges. | 0min |
| **Search** | Skip. Search owns its index, not state others care about. | 0min |
| **Audit** | Skip. Audit doesn't write business state. | 0min |
| **CheckoutOrchestrator** | Optional. Saga state transitions are interesting — declare publication on `checkout_sagas` if a consumer requests. | 0-30min |
| **BffWeb** | Skip. No state. | 0min |

Total producer integration: **~3 hours of pure DB-config work**, no code review of handler changes, no risk of forgotten outbox writes, no regression surface in business logic. Parallelizable trivially since each service's Postgres is independent.

The substantive work is in cdc-svc + the consumers. Producer integration is a footnote.

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

`wave run docs/agent-briefs/cdc-service-spec.md`. Mode: hybrid (introduces cdc-svc + Postgres config on every existing producer DB + modifies search/audit consumers). 8 disjoint-scope tracks:

| Track | Owns | Hours |
|---|---|---|
| **T1** cdc-svc skeleton + WAL replication subscriber | `src/Cdc/**` (new service via wave's new-service scaffold), Postgres logical replication client (`Npgsql.PostgresReplication` or equivalent), `pgoutput` decoder, `EntityChangedEvent` model + RabbitMQ publisher | 5 |
| **T2** cdc-svc admin API + `cdc` CLI | controllers (status, pause/resume/resync/replay/backfill), `tools/cdc` bash CLI mirroring wave/platform, runbook draft | 3 |
| **T3** Postgres replication configuration across all producer DBs | `infra/stateful/postgres-clusters/<service>.yaml` updates (`wal_level=logical` + `max_replication_slots`); per-service publication-creation SQL committed under `infra/stateful/cdc-publications/`; cdc_sources + cdc_table_map seed data | 3 |
| **T4** Cache invalidation consumer | new component, `ICdcEventHandler` impl, config-driven rules YAML (`infra/apps/cache-invalidator/rules.yaml`), Redis client, integration tests | 3 |
| **T5** Search consumer migration | refactor search-svc per `search-decoupling-spec.md` to consume `cdc.entity` events instead of typed Catalog events; the typed consumers retire | 3 |
| **T6** Audit data-mode consumer | new parallel `DataAuditConsumer` in audit-svc + `data_audit_events` table + integration tests | 3 |
| **T7** Webhooks + analytics consumer scaffolds (deferred services, hooks ready) | minimal `ICdcEventHandler` impl in webhooks-svc and analytics-svc directories so future services have a starting point. Even though those services aren't built, the consumer pattern is established here. | 2 |
| **T8** Monitoring + dashboards + alarms + E2E test | Prometheus rules + Grafana dashboard JSON under `infra/addons/grafana-dashboards/cdc.json`, the `CdcEndToEndJourney` test, the operations runbook completed | 3 |

**Disjoint-scope contract:**
- T1 owns `src/Cdc/**` (new service) and is the only writer to `EntityChangedEvent` schema.
- T2 owns the admin API + CLI; depends on T1 publishing the model but does not modify `src/Cdc/**` core relay code.
- T3 only touches infrastructure: `infra/stateful/postgres-clusters/*.yaml`, `infra/stateful/cdc-publications/*.sql`. Zero application code.
- T4-T7 each own a single consumer's directory with no overlap.
- T8 owns observability + tests + docs; integrates everything but adds no business logic.

**Total wall-clock with 8 agents in parallel: ~5 hours** (T1 is the longest track at 5h; everything else fits inside that).

After wave merges to `feat/cdc-platform`, integration smoke = the `CdcEndToEndJourney` test in T8. PR `feat/cdc-platform → main` is the rollup.

## 12. Reference projects to mirror

- `MassTransit` outbox docs — the library's design follows MassTransit's transactional outbox patterns
- `Debezium` documentation — for understanding the full CDC space (we're choosing simpler outbox-only for now)
- existing `src/BuildingBlocks/` patterns for the library shape
- existing `audit-svc` for what a CDC consumer looks like in this codebase
- `tools/wave` for the CLI shape that `tools/cdc` mirrors
