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

The application is unaware of CDC. Services just write to their tables. The DB does the work.

### 4.1 Topology — see `database-topology.md`

The full production DB topology, capacity limits, and Vault dynamic-credential flow are documented separately in [`../architecture/database-topology.md`](../architecture/database-topology.md). One-line summary for this spec: today, all 8 service databases live in one Fly Postgres instance (`ritualworks-vault-pg`); tomorrow (per k8s-platform-spec), each gets its own CNPG cluster.

**Both topologies look the same to cdc-svc** — each `(host, database)` pair is one source. Today: 8 entries on one host. Tomorrow: 8 distinct hosts. The relay, publications, and consumers don't know or care which.

### 4.2 Cluster-wide configuration (one change, all services covered)

`wal_level=logical` is a server-wide Postgres setting — flipping it once on `ritualworks-vault-pg` enables CDC for every database on that instance. Single change, all services covered.

Same for `max_replication_slots` — defaults to 10 on Fly Postgres. We need 8 slots (one per database) plus headroom for ad-hoc replays. Bump to 20.

Combined Fly Postgres config update:

```bash
fly postgres config update \
  --wal-level=logical \
  --max-replication-slots=20 \
  --max-wal-senders=20 \
  -a ritualworks-vault-pg
# requires a brief Postgres restart — schedule during low traffic
```

This is the **only host-level change** in the entire CDC rollout. No per-service DB modifications at this level.

For future CNPG topology, the same settings live in the per-service `Cluster` CR (`spec.postgresql.parameters.wal_level: logical`). One commit per cluster manifest.

### 4.3 Per-database publications (one statement per logical DB)

Each service's logical DB gets its own publication. Statements are cheap, run once per DB, no restart:

```sql
-- Run once against the 'identity' database
CREATE PUBLICATION cdc_publication FOR TABLE users, user_profiles
    WITH (publish = 'insert, update, delete');
ALTER TABLE users         REPLICA IDENTITY FULL;
ALTER TABLE user_profiles REPLICA IDENTITY FULL;

-- Repeat for catalog, orders, payments, content, checkout, audit, notifications
-- with their respective table lists.
```

Publication membership can be altered later via `ALTER PUBLICATION ADD TABLE` — no app restart, no DB restart, no consumer restart (cdc-svc picks up new tables on next WAL record).

Tables NOT in the publication are invisible to CDC — use this to exclude noisy or sensitive tables (e.g., audit's own `audit_events` should NOT be in audit's publication; that would be a feedback loop).

### 4.4 Application code change required

**None.** Services write to their tables as they always have. No new DI registration, no library to call, no outbox table, no transaction-scoping concern. The change is purely operational/infrastructural — a config flag on one Postgres instance plus 8 SQL statements (one per service DB).

### 4.5 Coexistence with domain events

Existing domain events stay. They carry **business intent** — "ProductPriceChangedEvent fired because of a promo rule" — which logical replication cannot infer. CDC carries **data history** — "products row updated, before=X after=Y". Both flow:
- Domain events → MassTransit → existing typed consumers (Notifications, Orders sagas, business handlers)
- CDC events → cdc-svc → generic consumers (cache, search, audit-data-mode, analytics, webhooks)

A consumer chooses based on what it needs. Most cross-cutting consumers want CDC (data sync); most business-flow consumers want domain events (semantic meaning).

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

### 7.1 Producers — pure infrastructure work, no application code

The production topology (single Fly Postgres, multiple logical DBs — see `architecture/database-topology.md`) makes this even smaller than the per-service-Postgres case would be:

**Step 1 — single cluster-wide change** (covers ALL services in one go):

```bash
fly postgres config update \
  --wal-level=logical \
  --max-replication-slots=20 \
  --max-wal-senders=20 \
  -a ritualworks-vault-pg
# requires brief restart of the shared Postgres — schedule in low-traffic window
```

**Step 2 — one publication per logical DB** (8 SQL statements, run by CI script):

| Logical DB | Publication tables | Effort |
|---|---|---|
| `catalog` | products, product_categories, product_prices | 5min |
| `orders` | orders, order_lines | 5min |
| `payments` | payments, payment_attempts, refunds | 5min |
| `identity` | users, user_profiles | 5min |
| `content` | content_items | 5min |
| `checkout` | checkout_sagas (optional, only if a consumer requests) | 0-5min |
| `audit` | SKIP — would create a feedback loop (audit's own data being captured by audit). | 0 |
| `notifications` | SKIP unless a real consumer emerges; notifications is pull-driven (consumes intent, doesn't produce state others react to). | 0 |
| `search` | SKIP — search owns its Meilisearch index, not state others react to. | 0 |
| `bffweb` | SKIP — no DB. | 0 |

Total producer integration: **~30 minutes of operator time** (one config update + 8 SQL statements committed to `infra/stateful/cdc-publications/`). The CI workflow that applies these is part of the cdc-svc rollout (T3 in § 11).

When the platform moves to per-service CNPG clusters (per `k8s-platform-spec.md` § 12 P2), the same publication SQL files apply unchanged — only the connection target changes from "ritualworks-vault-pg + database name" to "<service>-pg cluster".

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

## 12. Production DB integration + day-1 automation

The spec above describes the runtime architecture. This section is the operator-facing view: how the existing prod databases get CDC enabled, how every new service gets CDC by default, and what day-1 looks like for the operator.

### 12.1 Current prod DB topology — what we're working with

Today (May 2026), every service has its own Postgres on Fly:
- `ritualworks-<service>-pg` Fly app per service (per `fly.<service>-pg.toml` if present, else stand-alone Fly Postgres app)
- `wal_level` defaults to `replica` (Postgres standard) — needs upgrade to `logical` for CDC
- Vault dynamic credentials issue per-connection roles
- Connection strings injected via `ConnectionStrings__<service>` env in each service

Future (per `k8s-platform-spec.md`): CloudNativePG `Cluster` CR per service. CDC-aware from day 1.

### 12.2 Automation goal — three single commands

All three of these are idempotent (re-runnable, no-op if already correct):

```bash
# Day-1 enrolment for an existing service (works on Fly Postgres OR CNPG)
platform db cdc enable <service> [--tables T1,T2,...]
  # 1. SET wal_level=logical (Fly: fly postgres config; CNPG: patch Cluster CR; either restarts the DB)
  # 2. CREATE PUBLICATION cdc_publication FOR TABLE T1, T2, ... WITH (publish='insert,update,delete')
  # 3. ALTER TABLE <each> REPLICA IDENTITY FULL
  # 4. cdc-svc INSERT into cdc_sources + cdc_table_map (mapping derived from --tables or per defaults)
  # 5. emit acknowledgement: 'CDC enabled for <service>: 5 tables published, slot created'

platform db cdc add-table <service> <table> [--entity-type T]
  # ALTER PUBLICATION ADD TABLE; ALTER TABLE ... REPLICA IDENTITY FULL; insert cdc_table_map row.
  # No DB restart, no service restart.

platform db cdc disable <service>
  # Removes the publication, drops the slot in cdc-svc, deletes cdc_sources row.
  # wal_level stays logical (no DB restart cost; benign overhead).
```

These are NOT three new commands the operator types daily — they're invoked ONCE per service during the rollout. After that, the system runs itself.

### 12.3 New-service workflow — CDC by default

The wave tool's `apply_deploy_wiring` (already exists per `feat/wave-l0-deploy`) extends to also generate:

- `infra/stateful/cdc-publications/<service>.sql` — the publication script, derived from the service's domain entity tables (the wave's design pass identifies entity tables in the brief)
- A line in `cdc-svc`'s seed migration registering the service in `cdc_sources`
- Per-table `cdc_table_map` entries

Net effect: **a new service shipped via `wave run <spec>` is CDC-enrolled at first deploy.** The operator does not need to remember to `platform db cdc enable`. The "is this service CDC'd?" question disappears — every service is, by construction.

### 12.4 IaC anchor — `infra/stateful/cdc-publications/` is the source of truth

Per service: one `<service>.sql` file in this directory, declaratively listing the publication. Format:

```sql
-- infra/stateful/cdc-publications/catalog.sql
-- Idempotent: drops + recreates only on actual schema diff (CI-checked).
CREATE PUBLICATION IF NOT EXISTS cdc_publication FOR TABLE
    products,
    product_categories,
    product_prices,
    inventory_items
WITH (publish = 'insert, update, delete');

ALTER TABLE products            REPLICA IDENTITY FULL;
ALTER TABLE product_categories  REPLICA IDENTITY FULL;
ALTER TABLE product_prices      REPLICA IDENTITY FULL;
ALTER TABLE inventory_items     REPLICA IDENTITY FULL;
```

The CI workflow (`.github/workflows/cdc-publications.yml`) applies these on every merge to main:
1. Fly target: `flyctl postgres connect -a ritualworks-<svc>-pg < infra/stateful/cdc-publications/<svc>.sql`
2. CNPG target: `kubectl exec -n postgres-<svc> -c postgres -- psql -U postgres -f /tmp/cdc.sql` (after kubectl-cp'ing the file)

Same source file works for both topologies — that's the seamlessness.

### 12.5 cdc-svc itself — fully automated lifecycle

cdc-svc is just another wave-deployed service. Its production lifecycle:

| Concern | Mechanism |
|---|---|
| Provisioning | `wave run` produced its scaffold + Helm chart + fly.toml (Phase 1 of K8s platform). Deploys via the same path as audit/notifications/etc. |
| Slot creation | On startup, cdc-svc reads `cdc_sources`, opens a slot per source. Idempotent — existing slots reused. |
| Slot cleanup | On `cdc source disable`, slot is dropped from Postgres. No manual cleanup. |
| Backups | cdc-svc has zero durable state outside its own DB (`cdc_sources`, `cdc_table_map`, DLQ). Standard Velero backup of its namespace covers everything. WAL slots are NOT backed up — they're recreated from the source DB's current LSN if cdc-svc is restored. |
| Failover | cdc-svc runs as `replicas: 2` in prod with leader election (a single replica holds slots; the other is hot-standby). On primary failure, the standby acquires the slots within ~10s. Same pattern as the existing leader-elected hosted services in this codebase. |
| Upgrade | Helm `upgrade` rolls the cdc-svc image. New replica acquires slots before the old one releases — at-most a few seconds of relay pause. |

### 12.6 The seamless day-1 sequence

The shared-Postgres topology (see `architecture/database-topology.md`) means the producer-side work is one cluster config + applying the publication SQL files committed under `infra/stateful/cdc-publications/`:

```bash
# Step 1 — one cluster-wide config (covers all services on the shared instance)
platform db cdc enable-cluster              # under the hood:
                                            #   fly postgres config update --wal-level=logical
                                            #     --max-replication-slots=20 --max-wal-senders=20
                                            #     -a ritualworks-vault-pg
                                            # then runs every infra/stateful/cdc-publications/*.sql
                                            # against its target logical DB. Idempotent.

# Step 2 — deploy cdc-svc + consumers
platform deploy cdc                         # cdc-svc starts; opens 5 replication slots
platform deploy cache-invalidator           # new component
platform redeploy search audit              # existing services pick up CDC consumers from updated charts

# Step 3 — verify
platform db cdc status                      # shows 5 sources Healthy, lag < 1s, all consumers Active
platform doctor                             # full subsystem check
```

**Total operator effort: 5 commands.** Each idempotent. No editor sessions, no kubectl YAML hand-crafting, no SQL pasted into psql.

After the wave-tool extension lands (per § 12.3), every new service shipped via `wave run <spec>` is automatically CDC-enrolled at deploy time — its publication SQL is generated alongside its scaffold, and `platform db cdc enable-cluster` is a no-op because the cluster's already configured.

When the platform migrates to per-service CNPG clusters, only the implementation under `platform db cdc enable-cluster` changes (kubectl-patch each Cluster CR vs `fly postgres config update`); the operator surface stays the same.

### 12.7 Day-2 dashboard — single pane of glass

`platform db cdc status` output:

```
DB topology: 5 producers · 7 consumers · cdc-svc HA: 2 replicas (active=cdc-svc-0)

Sources                       slot         lag         pub-rate    last-event
  catalog                     active       0.4s        12 e/s      5s ago      ✓
  orders                      active       0.2s        8 e/s       3s ago      ✓
  payments                    active       0.1s        2 e/s       12s ago     ✓
  identity                    active       0.3s        0.1 e/s     2m ago      ✓
  content                     active       0.0s        0 e/s       8m ago      ✓

Consumers                     subscribed   lag         throughput  dlq
  cache-invalidator           5/5          0.5s        22 e/s      0           ✓
  search                      2/5 (cat,prd) 0.3s       12 e/s      0           ✓
  audit (data-mode)           5/5          0.4s        22 e/s      0           ✓
  webhooks                    5/5          1.1s        22 e/s      2           ⚠
  analytics                   not deployed
  ...

Overall: 22 events/s, e2e p99 = 1.4s, all green except webhooks DLQ

→ 2 events in webhooks DLQ — run 'cdc consumer dlq list webhooks'
```

One screen. Same view on Fly today, on CNPG tomorrow, on bare-metal k3s next month.

## 13. Reference projects to mirror

- `MassTransit` outbox docs — the library's design follows MassTransit's transactional outbox patterns
- `Debezium` documentation — for understanding the full CDC space (we're choosing simpler outbox-only for now)
- existing `src/BuildingBlocks/` patterns for the library shape
- existing `audit-svc` for what a CDC consumer looks like in this codebase
- `tools/wave` for the CLI shape that `tools/cdc` mirrors
