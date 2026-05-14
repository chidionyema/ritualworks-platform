# Search Service

## Overview

The Search service is the bounded context responsible for full-text product search and geospatial location search across the platform. It maintains a denormalized read model in Elasticsearch, kept up-to-date through two independent feed mechanisms: a **MassTransit/RabbitMQ consumer** that reacts to `ProductCacheInvalidatedEvent` and `CategoryUpdatedEvent`, and a **Kafka CDC worker** (`CdcSearchIndexWorker`) that consumes Debezium change events from the `db.catalog.public.products` and `db.catalog.public.categories` topics directly.

The service exposes an HTTP search API with optional category filtering, cursor-based result pagination, saved-search registration, and Elasticsearch percolation (reverse search) that fires `ProductMatchedSavedSearchEvent` when a newly indexed product matches a stored query.

Bounded context: **Search** — the service owns no primary data. It projects data from Catalog (via RabbitMQ events and Kafka CDC) and Location (via `LocationUpdated` events). Its Elasticsearch index is a derivative, reconstructible read model.

---

## Architecture

| Layer | Project | Responsibility |
|---|---|---|
| Domain | `Search.Domain` | (Thin — no entities; domain logic lives in Application) |
| Application | `Search.Application` | `ISearchIndex`, `ILocationSearchIndex` interfaces, `SearchQuery`, `ProductSearchDocument`, `LocationSearchDocument`, consumers, projectors, CDC worker |
| Infrastructure | `Search.Infrastructure` | `ElasticsearchIndex` (Elastic .NET client v8), `LocationSearchIndex`, `CatalogProductsApiClient`, MassTransit wiring |
| API | `Search.Api` | `SearchController`, JWT authentication |

**Key dependencies:**
- **Elastic.Clients.Elasticsearch v8** — index management, bulk upsert, multi-match search, percolator queries
- **MassTransit 8 + RabbitMQ** — `ProductCacheInvalidatedConsumer`, `CategoryUpdatedConsumer`, `LocationUpdatedConsumer`
- **Confluent.Kafka / .NET Aspire Kafka integration** — `CdcSearchIndexWorker` reads Debezium CDC topics
- **`ICatalogProductsApi`** (HTTP client) — fetches full product data from the Catalog service on cache invalidation events
- **Polly** — resilience policy wrapping Catalog HTTP calls
- **Serilog + OpenTelemetry** — structured logging and distributed tracing (`SearchActivities` source)

There is no database. The Elasticsearch index is the sole persistent store.

---

## Domain Model

Search has no traditional domain entities or aggregates. Its read model consists of two document types indexed in Elasticsearch.

### `ProductSearchDocument`

Stored in the `products` index (configurable via `Elasticsearch:IndexName`).

| Field | Type | Description |
|---|---|---|
| `ProductIdKey` | `string` | Dash-free UUID (`Guid.ToString("N")`) — Elasticsearch document ID |
| `ProductId` | `string` | Original UUID string, returned to API callers |
| `Name` | `string` | Boosted field (weight 3) in multi-match queries |
| `Description` | `string` | Standard search field |
| `CategoryId` | `string` | Keyword field; used for category filter |
| `CategoryName` | `string` | Denormalized category name (weight 2); updated by `CategoryUpdatedConsumer` |
| `UnitPrice` | `decimal` | Current price |
| `IsInStock` | `bool` | Stock flag |
| `IsListed` | `bool` | Visibility flag |
| `SourceVersion` | `long` | Row version for out-of-order event suppression — stale events are dropped if `existing.SourceVersion >= incoming` |
| `IndexedAt` | `long` | Unix epoch seconds; used as freshness tiebreaker in ranking |

### `LocationSearchDocument`

Stored in the Elasticsearch `locations` index (managed by `LocationSearchIndex`).

| Field | Type | Description |
|---|---|---|
| `LocationId` | `string` | Opaque location identifier |
| `Location` | `GeoPoint` | Latitude + longitude for geo_point mapping |
| `Postcode` | `string?` | Optional postcode |
| `Metadata` | `Dictionary<string,string>` | Extensible key-value metadata |

### Saved Searches (Percolator)

Saved searches are stored in the `saved_searches` Elasticsearch index using the percolator field type. When a product is indexed, `PercolateAsync` runs the document against all stored queries and publishes `ProductMatchedSavedSearchEvent` for each match.

---

## API Endpoints

Authentication is required for all endpoints (JWT via `AddPlatformAuthentication`).

| Method | Route | Auth | Description |
|---|---|---|---|
| `GET` | `/search` | JWT | Full-text product search. Query is sanitized (`SearchQuerySanitizer`) before being sent to Elasticsearch as a fuzzy multi-match across `name^3`, `categoryName^2`, `description`. Supports optional `categoryId` filter. |
| `POST` | `/search/saved` | JWT | Register a saved search query for the authenticated user. Stored in Elasticsearch percolator index. Returns the saved search ID. |

**Query parameters for `GET /search`:**

| Parameter | Type | Constraints | Default | Description |
|---|---|---|---|---|
| `q` | `string` | Required, 1–200 chars | — | Search query |
| `categoryId` | `Guid?` | Optional | — | Filter to a single category |
| `page` | `int` | 1–10,000 | `1` | Page number |
| `pageSize` | `int` | 1–100 | `20` | Results per page |

**Response** includes `query`, `categoryId`, `page`, `pageSize`, `totalHits`, `tookMs`, and a `hits` array with `productId`, `name`, `snippet`, `categoryId`, `categoryName`, `unitPrice`, `isInStock`, `score`.

---

## Events

### Consumed (via RabbitMQ / MassTransit)

| Event | Contract | Consumer | Description |
|---|---|---|---|
| `ProductCacheInvalidatedEvent` | `Haworks.Contracts.Catalog` | `ProductCacheInvalidatedConsumer` | Upserts or deletes the product document. On upsert, fetches the full product from the Catalog HTTP API. Suppresses out-of-order events using `SourceVersion`. On upsert, runs percolation and publishes `ProductMatchedSavedSearchEvent` for matches. |
| `CategoryUpdatedEvent` | `Haworks.Contracts.Catalog` | `CategoryUpdatedConsumer` | Re-denormalizes `CategoryName` across all product documents in that category. Paginates in batches of 1,000. |
| `LocationUpdated` | `Haworks.Contracts.Location` | `LocationUpdatedConsumer` | Upserts the location document in the geospatial index. |

### Consumed (via Kafka CDC — `CdcSearchIndexWorker`)

| Kafka Topic | Description |
|---|---|
| `db.catalog.public.products` | Debezium CDC events for product row changes. Supports `c` (create), `r` (snapshot read), `u` (update), `d` (delete) operations. |
| `db.catalog.public.categories` | Debezium CDC events for category row changes. Category renames log a warning; full re-denormalization is not yet implemented for the CDC path. |

The Kafka consumer uses consumer group `search-svc-cdc` with `AutoOffsetReset.Earliest`. It is disabled in the `Test` environment.

### Published

| Event | Contract | Trigger |
|---|---|---|
| `ProductMatchedSavedSearchEvent` | `Haworks.Contracts.Search` | Published for each saved search that matches a newly indexed product document (percolation result). Contains `SavedSearchId`, `UserId`, `ProductId`, `ProductName`, `UnitPrice`, `MatchedAt`. |

---

## Configuration

### `Elasticsearch` section (`ElasticsearchOptions`)

| Key | Type | Required | Default | Description |
|---|---|---|---|---|
| `Elasticsearch:Url` | `string` | Yes | — | Elasticsearch node URL (e.g. `http://localhost:9200`) |
| `Elasticsearch:IndexName` | `string` | No | `products` | Name of the product search index |

### Connection strings

| Key | Description |
|---|---|
| `ConnectionStrings:rabbitmq` | RabbitMQ AMQP URI (e.g. `amqp://guest:guest@localhost:5672/`) |
| `ConnectionStrings:kafka` | Kafka bootstrap servers (via .NET Aspire Kafka integration) |

### HTTP client

| Key | Default | Description |
|---|---|---|
| `Catalog:BaseAddress` | `http://ritualworks-catalog.flycast:8080` | Base URL for the Catalog service HTTP client. Timeout is 5 seconds. |

### Platform configuration (inherited)

| Key | Description |
|---|---|
| `Authentication:JwksUri` | JWKS endpoint for JWT validation |

### Elasticsearch index bootstrap

On startup, `ISearchIndex.EnsureSettingsAsync()` is called. If Elasticsearch is unavailable, a warning is logged and the app continues (it will retry on the next cold start). The method creates:
- The `products` index with keyword mappings for `ProductIdKey` and `CategoryId`, and text mappings for `Name`, `Description`, `CategoryName`.
- The `saved_searches` percolator index with `userId`, `name`, `categoryName`, `description` fields and a `query` percolator property.

---

## Database

The Search service has **no relational database**. All persistent state is held in Elasticsearch.

**Elasticsearch indices:**

| Index | Description |
|---|---|
| `products` (configurable) | Product search documents. Primary key: `ProductIdKey`. Supports multi-match full-text search with fuzzy matching and category keyword filter. |
| `saved_searches` | Percolator index. Stores user-defined queries. Queried via Elasticsearch percolator when a new product is indexed. |
| `locations` | Geospatial index for location search documents (`LocationSearchIndex`). |

---

## Testing

### Test projects

| Project | Path | Description |
|---|---|---|
| `Search.Unit` | `tests/Search/Search.Unit` | Unit tests for consumers, projectors, query sanitizer, document projection |
| `Search.Integration` | `tests/Search/Search.Integration` | Integration tests against real Elasticsearch |

### Running tests

```bash
# Unit tests (no external dependencies)
dotnet test tests/Search/Search.Unit

# Integration tests (requires Docker)
dotnet test tests/Search/Search.Integration

# All Search tests
dotnet test tests/Search/
```

### Integration test infrastructure

Integration tests use the shared Testcontainers singleton from `BuildingBlocks.Testing.Containers`:

```csharp
var connection = await SharedTestElasticsearch.GetConnectionAsync("search");
```

Do not instantiate raw Elasticsearch containers. The CI architecture check (`scripts/check-architecture.sh`) enforces this.

MassTransit consumers are tested using `AddMassTransitTestHarness` in the `SearchWebAppFactory`; the RabbitMQ and Kafka transports are skipped in the `Test` environment.
