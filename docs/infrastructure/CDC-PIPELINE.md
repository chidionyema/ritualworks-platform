# CDC Pipeline

## Overview

Change Data Capture (CDC) is implemented using Debezium Connect reading the PostgreSQL Write-Ahead Log (WAL) and publishing change events to Apache Kafka. Three downstream consumers read from Kafka topics independently: the Search service updates its Elasticsearch index, the BFF invalidates its distributed cache, and the Webhooks service fans out events to registered subscribers.

The CDC pipeline replaced a custom in-process CDC service (which was removed). It introduces no additional coupling between services: producers (Postgres databases) are unaware of consumers, and consumers are unaware of each other.

---

## Architecture

```
PostgreSQL WAL
    |
    | (logical replication via pgoutput plugin)
    v
Debezium Connect (debezium/connect:3.0)
    |
    | JSON messages, schemas disabled
    v
Apache Kafka (bitnami/kafka:3.7, KRaft mode)
    |
    |-- db.catalog.public.products --------> Search (CdcSearchIndexWorker)
    |                                         BFF (BffCdcCacheInvalidator)
    |                                         Webhooks (CdcFanOutWorker)
    |
    |-- db.catalog.public.categories ------> Search (CdcSearchIndexWorker)
    |
    |-- db.catalog.public.product_categories -> Webhooks (CdcFanOutWorker)
    |
    |-- db.orders.public.orders ------------> Webhooks (CdcFanOutWorker)
    |
    |-- db.payments.public.payments --------> Webhooks (CdcFanOutWorker)
```

Postgres is configured with `wal_level=logical`, `max_replication_slots=10`, and `max_wal_senders=10`. Each Debezium connector creates a named replication slot and publication on first connect.

---

## Debezium connector configurations

Connector configurations live in `deploy/aspire/debezium/` and are registered against the Debezium Connect REST API at startup by the `debezium-init` one-shot container.

All three connectors share the same structure. The connector class is `io.debezium.connector.postgresql.PostgresConnector` using the `pgoutput` logical decoding plugin. Schemas are disabled on both key and value converters so Kafka messages are plain JSON without embedded schema payloads.

### catalog-connector.json

```json
{
  "connector.class": "io.debezium.connector.postgresql.PostgresConnector",
  "database.hostname": "postgres",
  "database.port": "5432",
  "database.user": "postgres",
  "database.password": "postgres",
  "database.dbname": "catalog",
  "topic.prefix": "db.catalog",
  "schema.include.list": "public",
  "plugin.name": "pgoutput",
  "slot.name": "debezium_catalog",
  "publication.name": "dbz_catalog_pub",
  "publication.autocreate.mode": "filtered",
  "key.converter": "org.apache.kafka.connect.json.JsonConverter",
  "key.converter.schemas.enable": false,
  "value.converter": "org.apache.kafka.connect.json.JsonConverter",
  "value.converter.schemas.enable": false,
  "snapshot.mode": "initial"
}
```

### orders-connector.json

Identical structure with `database.dbname: "orders"`, `topic.prefix: "db.orders"`, `slot.name: "debezium_orders"`, `publication.name: "dbz_orders_pub"`.

### payments-connector.json

Identical structure with `database.dbname: "payments"`, `topic.prefix: "db.payments"`, `slot.name: "debezium_payments"`, `publication.name: "dbz_payments_pub"`.

### Topic naming convention

Debezium constructs topic names as `<topic.prefix>.<schema>.<table>`. With `topic.prefix=db.catalog` and `schema.include.list=public`, the catalog connector publishes to:

- `db.catalog.public.products`
- `db.catalog.public.categories`
- `db.catalog.public.product_categories`
- (any other tables in the `public` schema of the `catalog` database)

---

## Kafka topics and message format

### DebeziumEnvelope

The `DebeziumEnvelope` record in `src/Contracts/Cdc/DebeziumEnvelope.cs` is the canonical deserialization target for all CDC consumers:

```csharp
public sealed record DebeziumEnvelope(
    JsonElement? Before,   // row state before the change; null for inserts
    JsonElement? After,    // row state after the change; null for deletes
    string Op,             // "c" = insert, "u" = update, "d" = delete, "r" = snapshot read
    long TsMs,             // event timestamp (milliseconds since epoch)
    DebeziumSource? Source // source metadata
);

public sealed record DebeziumSource(
    string? Db,     // database name
    string? Schema, // schema name
    string? Table,  // table name
    long? TxId      // Postgres transaction ID
);
```

### Operation codes

| `Op` value | Meaning |
|---|---|
| `c` | INSERT (create) |
| `u` | UPDATE |
| `d` | DELETE |
| `r` | Snapshot read (initial snapshot) |

Consumers map these to human-readable strings using the same pattern:

```
"c" -> "created"
"r" -> "created"  (snapshot)
"u" -> "updated"
"d" -> "deleted"
```

### Example message (product insert)

```json
{
  "before": null,
  "after": {
    "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
    "name": "Ritual Candle",
    "description": "Hand-poured soy wax",
    "unit_price": 24.99,
    "category_id": "a1b2c3d4-...",
    "is_listed": true
  },
  "op": "c",
  "ts_ms": 1747228800000,
  "source": {
    "db": "catalog",
    "schema": "public",
    "table": "products",
    "txId": 1234
  }
}
```

---

## Consumers

### Search: CdcSearchIndexWorker

Location: `src/Search/Search.Application/Consumers/CdcSearchIndexWorker.cs`

Implements `BackgroundService`. Subscribes to:

- `db.catalog.public.products`
- `db.catalog.public.categories`

**Products**: On insert or update, deserializes the `after` payload into a `ProductSearchDocument` and upserts it into Elasticsearch via `ISearchIndex.UpsertAsync()`. On delete, reads the `id` from the `before` payload and calls `ISearchIndex.DeleteAsync()`.

**Categories**: On update, logs the category rename. Full re-denormalization of product documents carrying the old category name is not yet implemented (noted in code with `await Task.CompletedTask`).

Commits offsets manually after each message is processed successfully.

### BFF: BffCdcCacheInvalidator

Location: `src/BffWeb/BffWeb.Application/Consumers/BffCdcCacheInvalidator.cs`

Implements `BackgroundService`. Subscribes to:

- `db.catalog.public.products`

On any operation (insert, update, delete), extracts the `id` field from the `after` payload (or `before` for deletes) and removes the cache key `product_detail_<id>` from `IDistributedCache`. This ensures the BFF's L2 cache does not serve stale product data after a catalog change propagates through Debezium.

Consumer group ID: `bff-web-cdc`

### Webhooks: CdcFanOutWorker

Location: `src/Webhooks/Webhooks.Infrastructure/Workers/CdcFanOutWorker.cs`

Implements `BackgroundService`. Subscribes to:

- `db.catalog.public.products`
- `db.catalog.public.product_categories`
- `db.orders.public.orders`
- `db.payments.public.payments`

On each message:

1. Derives the event name as `<table>.<op>` (e.g., `products.updated`, `orders.created`).
2. Queries `IWebhooksDbContext.Subscriptions` for active subscriptions that include the event name.
3. For each matching subscription, creates a `WebhookDelivery` record and persists it.
4. Enqueues a Hangfire background job (`IWebhookDispatcher.DispatchAsync`) per delivery.

The delivery payload is a JSON object containing the event name, a generated event ID, the delivery timestamp, and the `after` (or `before` for deletes) payload from the envelope.

Consumer group ID: `webhooks-svc-cdc`

---

## Operations: cdc.sh CLI

The `scripts/cdc.sh` script provides a command-line interface to the Debezium Connect REST API.

The default Connect URL is `http://localhost:8083`. Override with `CONNECT_URL`:

```bash
export CONNECT_URL=http://my-connect-host:8083
```

### Commands

**Show all connectors and their states:**

```bash
bash scripts/cdc.sh status
```

Output columns: `CONNECTOR`, `STATE`, `WORKER`, `TASKS`, `TASK_STATES`

**Pause a connector** (stops consuming WAL, replication slot remains open):

```bash
bash scripts/cdc.sh pause catalog-connector
```

**Resume a paused connector:**

```bash
bash scripts/cdc.sh resume catalog-connector
```

**Register or update a connector from a JSON file** (idempotent PUT):

```bash
bash scripts/cdc.sh register deploy/aspire/debezium/catalog-connector.json
```

The connector name is derived from the filename with `-connector` suffix stripped.

**Delete a connector** (also drops the replication slot):

```bash
bash scripts/cdc.sh delete catalog-connector
```

**List topic prefixes and databases per connector:**

```bash
bash scripts/cdc.sh topics
```

---

## Failure modes and recovery

### Debezium Connect restarts

Debezium stores connector configurations, offsets, and task statuses in three dedicated Kafka topics:

- `debezium_configs`
- `debezium_offsets`
- `debezium_statuses`

On restart, Debezium reads these topics and resumes from the last committed WAL offset. No events are lost as long as the Kafka topics are durable and the Postgres replication slot is still open.

### Replication slot accumulation

An open replication slot prevents Postgres from reclaiming WAL segments. If Debezium Connect is stopped for an extended period, WAL will accumulate on disk. Monitor `pg_replication_slots` for slots with large `lag`:

```sql
SELECT slot_name, active, pg_size_pretty(pg_wal_lsn_diff(pg_current_wal_lsn(), restart_lsn)) AS lag
FROM pg_replication_slots;
```

If a connector is permanently removed, delete its slot explicitly:

```sql
SELECT pg_drop_replication_slot('debezium_catalog');
```

### Consumer lag

Each consumer commits offsets after processing each message. A crash mid-processing will replay the last uncommitted message on restart, causing at-least-once delivery. Consumers are designed to be idempotent:

- Search upserts (safe to apply twice).
- BFF cache invalidation (removing a key that is already absent is a no-op).
- Webhooks fan-out: duplicate `WebhookDelivery` records may be created for a single Kafka message in a crash-recovery scenario. The `IWebhookDispatcher` is expected to handle duplicate delivery IDs.

### Snapshot mode

All connectors use `snapshot.mode: initial`. On the first run (or after a connector is deleted and re-registered), Debezium performs a full table snapshot before switching to streaming. Snapshot reads produce messages with `op: "r"`. Consumers treat `"r"` the same as `"c"`.

### Kafka unavailability

If Kafka is unavailable, all three consumer workers will block at `consumer.Consume(stoppingToken)` until Kafka returns. No data is lost as Postgres WAL continues to accumulate on the replication slot. When Kafka recovers, Debezium resumes streaming from where it left off.

### Elasticsearch unavailability

`CdcSearchIndexWorker` will log an error and continue to the next message if an Elasticsearch operation fails. The failed message will be committed, meaning the index update is skipped. For sustained Elasticsearch outages, consider pausing the connector and replaying from a known-good offset after recovery.
