# Catalog Service

## Overview

The Catalog service is the system of record for products, categories, stock levels, and product reviews. It participates in the CheckoutSaga as the stock reservation authority: it processes `StockReservationRequestedEvent` to decrement stock and publishes `StockReservedEvent` or `StockReservationFailedEvent`, and handles `StockReleaseRequestedEvent` for compensation when payment fails.

Bounded context: **Catalog**. No other service writes to the `catalog` schema. Other services receive product information through events and maintain their own read-model snapshots.

---

## Architecture

Clean Architecture with four projects:

| Layer | Project | Responsibility |
|---|---|---|
| Domain | `Catalog.Domain` | `Product`, `Category`, `ProductReview`, `ProductMetadata`, `ProductSpecification`, `StockReservation` aggregates; repository interfaces; `InsufficientStockException` |
| Application | `Catalog.Application` | MediatR commands/queries, MassTransit consumers, FluentValidation validators, DTOs, caching interface, reservation metrics interface |
| Infrastructure | `Catalog.Infrastructure` | `CatalogDbContext`, repository implementations, `ProductCacheReader` (HybridCache), `ReservationSweeperService` (background service), metrics, consumer definitions |
| API | `Catalog.Api` | ASP.NET Core controllers, idempotency middleware, HybridCache registration, instance-ID response header middleware |

**Key dependencies:**
- MediatR (CQRS dispatch)
- MassTransit 8.x + RabbitMQ (outbox-backed event publishing, inbox deduplication)
- EF Core 9 with xmin shadow concurrency tokens (PostgreSQL) for optimistic locking on `Product` stock mutations
- `Haworks.BuildingBlocks.Idempotency` — `X-Idempotency-Key` middleware (PostgreSQL-backed, scoped to `CatalogDbContext`)
- `Haworks.BuildingBlocks.Authentication` — platform JWT validation (`AddPlatformAuthentication`)
- HybridCache (in-memory L1; Redis L2 ready) for product read-through caching
- `ReservationSweeperService` — background service that expires stale `Pending` reservations and returns stock
- OpenTelemetry tracing via `CatalogActivities` source (`catalog.reservation.create`, etc.)

---

## Domain Model

### Product (`Catalog.Domain.Product`)
Aggregate root. Owns its stock count — stock mutation is atomic with the EF xmin concurrency check. Private setters; all mutations via domain methods.

Key properties: `Name`, `Description`, `UnitPrice`, `StockQuantity`, `IsInStock`, `IsListed`, `CategoryId`, `Reviews`, `Metadata`, `Specifications`.

Domain methods:
- `Create(name, description, unitPrice, categoryId)` — validates non-negative price
- `UpdateBasicInfo(name, description)`
- `UpdatePricing(unitPrice)` — rejects negative prices
- `RestockTo(quantity)` — sets absolute stock level; sets `IsInStock`
- `ReserveStock(quantity)` — decrements stock; returns `false` if insufficient; EF xmin concurrency token ensures concurrent reservers fail-fast with `DbUpdateConcurrencyException`
- `ReleaseStock(quantity)` — increments stock (compensation path)
- `List()` / `Unlist()` — visibility toggle
- `AddMetadata(key, value)` / `AddSpecification(name, value, displayOrder)`

**Invariant:** price cannot be negative; quantity must be positive for reserve/release.

### Category (`Catalog.Domain.Category`)
Simple aggregate: `Name`, `Description`, collection of `Product`. `Create(name, description?)` enforces non-empty name. `Rename(name)` is the only mutation.

### StockReservation (`Catalog.Domain.StockReservation`)
Tracks pre-order inventory holds. State machine:

```
Pending --Confirm()--> Confirmed
Pending --Expire()---> Expired
```

Factory methods:
- `Create(userId, itemsJson, ttl)` — creates `Pending` reservation with TTL; used in synchronous ADR-004 checkout flow
- `CreateConfirmed(orderId, sagaId, userId, itemsJson)` — saga path; skips Pending, goes straight to Confirmed
- `MarkReleased(reason)` — bookkeeping for compensation / sweeper

Fields: `UserId`, `OrderId` (nullable until confirm), `SagaId` (nullable until confirm), `ItemsJson`, `Status`, `ReservedAt`, `ExpiresAt`, `ConfirmedAt`, `ExpiredAt`, `ReleasedAt`, `ReleaseReason`.

### ProductReview (`Catalog.Domain.ProductReview`)
Customer review with approval workflow. Associated with `Product`.

### ProductMetadata / ProductSpecification
Value-like child entities of `Product` holding key-value metadata and display-ordered specifications respectively.

### InsufficientStockException
Domain exception thrown when reservation preconditions fail outside the `ReserveStock` return-value path.

---

## API Endpoints

### Products (`/api/products`)

| Method | Route | Auth | Description |
|---|---|---|---|
| GET | `/api/products` | None | List products; query params: `skip`, `take`, `categoryId` |
| GET | `/api/products/{id}` | None | Get product by ID |
| GET | `/api/products/{id}/cached` | None | Get product via HybridCache read-through; response includes `source` (`L1`/`database`) and `latencyMs` |
| POST | `/api/products` | None | Create product |
| PUT | `/api/products/{id}` | None | Update product (name, description, price, category, listing status) |
| DELETE | `/api/products/{id}` | None | Delete product; optional `correlationId` query param |
| POST | `/api/products/{id}/reserve` | None | Reserve stock for a specific product (saga path) |

### Categories (`/api/categories`)

| Method | Route | Auth | Description |
|---|---|---|---|
| GET | `/api/categories` | None | List all categories |
| POST | `/api/categories` | None | Create category |

### Reservations (`/api/checkout/reservations`)

| Method | Route | Auth | Description |
|---|---|---|---|
| POST | `/api/checkout/reservations` | None (anonymous) | Create inventory hold (15-min TTL); `X-Idempotency-Key` header supported; BFF-forwarded user ID used if present, otherwise `"guest"` |
| POST | `/api/checkout/reservations/{reservationId}/confirm` | Bearer JWT + email claim | Confirm reservation, bind to `OrderId`; returns 410 if expired |

### Product Reviews

| Method | Route | Auth | Description |
|---|---|---|---|
| GET | `/api/products/{id}/reviews` | None | List reviews for product |
| GET | `/api/productreviews/{id}` | None | Get single review |
| POST | `/api/productreviews` | None | Submit review |
| PUT | `/api/productreviews/{id}` | None | Update review |
| DELETE | `/api/productreviews/{id}` | None | Delete review |
| POST | `/api/productreviews/{id}/approve` | None | Approve review (admin) |

---

## Events

### Published

| Event | Trigger | Description |
|---|---|---|
| `StockReservedEvent` | `StockReservationRequestedConsumer` — all items available | Carries `OrderId`, `SagaId`, `UserId`, `TotalAmount`, `Currency`, `CustomerEmail`, `Items`, `OrderLineItems` |
| `StockReservationFailedEvent` | `StockReservationRequestedConsumer` — any item unavailable | Carries `OrderId`, `SagaId`, `FailedItems`, `Reason` |
| `StockReleasedEvent` | `StockReleaseRequestedConsumer` — compensation path | Carries `OrderId`, `Items`, `Reason` |

All events are published via `IDomainEventPublisher` before `SaveChangesAsync`, so the `OutboxMessage` row commits in the same EF transaction as the stock mutation.

### Consumed

| Event | Consumer | Action |
|---|---|---|
| `StockReservationRequestedEvent` | `StockReservationRequestedConsumer` | Reserve stock for each item; publish `StockReservedEvent` or `StockReservationFailedEvent` |
| `StockReleaseRequestedEvent` | `StockReleaseRequestedConsumer` | Return reserved stock; publish `StockReleasedEvent` |
| `StockReleaseRequestedEvent` (fault) | `StockReleaseFaultConsumer` | Handle fault on release consumer |

**Idempotency:** MassTransit inbox deduplicates by `MessageId` at transport level. EF xmin shadow concurrency token (`WHERE xmin = N`) catches concurrent writers on `Product`.

---

## Configuration

| Key | Description |
|---|---|
| `ConnectionStrings:CatalogDb` | PostgreSQL connection string |
| `RabbitMQ:Host` / credentials | MassTransit transport |
| `Reservation:SweepIntervalSeconds` | How often the sweeper checks for expired reservations |
| `Reservation:DefaultTtlMinutes` | TTL for new `Pending` reservations (default: 15) |

Authentication is platform-shared: downstream services provide JWKS URL via `AddPlatformAuthentication`.

---

## Database

- **Schema:** `catalog`
- **DbContext:** `CatalogDbContext`
- **Migration runner:** `MigrateWithRetryAsync` on startup (skipped in `Test` environment)

### Key tables

| Table | Description |
|---|---|
| `catalog.Products` | Products with stock, pricing, listing status; xmin concurrency token |
| `catalog.Categories` | Product categories |
| `catalog.ProductReviews` | Customer reviews with approval status |
| `catalog.ProductMetadata` | Key-value metadata for products |
| `catalog.ProductSpecifications` | Display-ordered specifications |
| `catalog.StockReservations` | Inventory holds with lifecycle status |
| `catalog.OutboxMessages` | MassTransit transactional outbox |
| `catalog.OutboxState` | Outbox delivery state |
| `catalog.InboxState` | Inbox deduplication |

### Migrations

| Migration | Date | Description |
|---|---|---|
| `20260503145707_InitialCreate` | 2026-05-03 | Products, Categories, Reviews |
| `20260503152932_AddOutboxAndXminConcurrency` | 2026-05-03 | MassTransit outbox/inbox tables; xmin shadow property on Products |
| `20260503232850_AddProductMetadataAndStockReservation` | 2026-05-03 | ProductMetadata, ProductSpecification, StockReservation |
| `20260509020612_AddStockReservationLifecycle` | 2026-05-09 | StockReservation lifecycle fields (ConfirmedAt, ExpiredAt, ReleasedAt, ReleaseReason, SagaId) |

---

## Testing

### Test projects

| Project | Location | Coverage |
|---|---|---|
| `Catalog.Unit` | `tests/Catalog/Catalog.Unit/` | Domain model (`ProductTests`, `StockReservationTests`), command handlers (UpdateProduct, DeleteProduct, CreateReservation, ConfirmReservation), query handlers (GetProductById, ListProducts, ListCategories), consumers (`StockReleaseFaultConsumer`, retry harness), validators |
| `Catalog.Integration` | `tests/Catalog/Catalog.Integration/` | HTTP flows (`CatalogFlowsTests`), auth enforcement (`AuthTests`), category events (`CategoryEventsTests`), product reviews (`ProductReviewTests`), reservation endpoints (`ReservationEndpointTests`), sweeper (`ReservationSweeperTests`) via `CatalogWebAppFactory` |
| `Catalog.Architecture` | `tests/Catalog/Catalog.Architecture/` | Dependency boundary enforcement |
| `Catalog.Contract` | `tests/Catalog/Catalog.Contract/` | Pact consumer contract tests (`StockReservedConsumerTests`) |

### Running tests

```bash
# Unit tests
dotnet test tests/Catalog/Catalog.Unit/

# Integration tests (requires Docker)
dotnet test tests/Catalog/Catalog.Integration/

# Architecture tests
dotnet test tests/Catalog/Catalog.Architecture/
```

Integration tests use `SharedTestPostgres.CreateDatabaseAsync("catalog")` from `BuildingBlocks.Testing.Containers`. Raw `PostgreSqlBuilder` or `ContainerBuilder` usage is prohibited and enforced by `scripts/check-architecture.sh`.
