# Catalog Service

Manages product listings, inventory, and stock reservation for the checkout saga.

## Responsibilities
- Full CRUD for products with hybrid L1/L2 caching
- Atomic stock reservation and release driven by MassTransit consumers
- Sweep stale reservations via `ReservationSweeperService`

## API Endpoints
| Method | Route | Notes |
|--------|-------|-------|
| GET | `/api/products` | List products |
| GET | `/api/products/{id}` | Get by id |
| GET | `/api/products/cached` | Cached read path |
| POST | `/api/products` | Create product |
| PUT | `/api/products/{id}` | Update product |
| DELETE | `/api/products/{id}` | Delete product |
| POST | `/api/products/{id}/reserve` | Manual reserve |

## Domain Entities
- **Product** — `ReserveStock(int qty)` / `ReleaseStock(int qty)`; EF `xmin` optimistic concurrency

## Events Consumed
- `StockReservationRequestedEvent` → publishes `StockReservedEvent` or `StockReservationFailedEvent`
- `StockReleaseRequestedEvent`

## Events Published
- `StockReservedEvent`
- `StockReservationFailedEvent`

## Infrastructure Dependencies
- PostgreSQL (`CatalogDbContext`) with Vault dynamic credentials
- RabbitMQ via MassTransit (transactional outbox)
- HybridCache (in-process + distributed)

## Configuration
```
ConnectionStrings:catalog
Vault:Enabled / RoleId / SecretId
RabbitMq:Host / Username / Password
```

## Health Checks
- DB: `AddDbHealthCheck<CatalogDbContext>()`
