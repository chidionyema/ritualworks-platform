# Orders Service

## Overview

The Orders service is the system of record for the order lifecycle. It creates orders in response to saga events and transitions them through states as payment and stock events arrive. It does not initiate payments or touch inventory — it is a pure event-driven projection of the checkout saga's outcome.

Bounded context: **Orders**. No other service writes to the `orders` schema. The service maintains its own read model of product names and prices (snapshotted at order time) and carries opaque foreign keys to `identity-svc` (`UserId`) and `payments-svc` (`PaymentId`).

---

## Architecture

Clean Architecture with four projects:

| Layer | Project | Responsibility |
|---|---|---|
| Domain | `Orders.Domain` | `Order`, `OrderItem`, `GuestOrderInfo`, `StockReleaseFailure` aggregates; `OrderStatus` enum; repository interface |
| Application | `Orders.Application` | `CreateOrderCommand`, MassTransit consumers, query handlers, FluentValidation validators, DTOs |
| Infrastructure | `Orders.Infrastructure` | `OrderDbContext`, `OrderRepository`, consumer definitions, MassTransit outbox/inbox |
| API | `Orders.Api` | `OrdersController`, `DemoIdempotencyController`, idempotency middleware, Serilog |

**Key dependencies:**
- MediatR (CQRS dispatch)
- MassTransit 8.x + RabbitMQ (transactional outbox, inbox deduplication)
- EF Core 9 with xmin shadow concurrency token on `Orders` table
- `Haworks.BuildingBlocks.Idempotency` — `X-Idempotency-Key` middleware (PostgreSQL-backed, scoped to `OrderDbContext`)
- `Haworks.BuildingBlocks.Authentication` — platform JWT validation
- OpenTelemetry tracing via `OrdersActivities` source (`orders.create`, `orders.complete`)

---

## Domain Model

### Order (`Orders.Domain.Order`)
Aggregate root. Represents a customer's purchase. Private setters; all mutations through domain methods.

Key properties: `UserId` (opaque string FK to identity-svc), `SagaId` (checkout saga correlation), `IdempotencyKey`, `CustomerEmail`, `TotalAmount`, `Currency`, `Status`, `PaymentId` (nullable, set on payment), `AbandonReason` (nullable, set on abandonment), `Items` (collection of `OrderItem`).

**State machine:**

```
Created --MarkPaid(paymentId)-----------> Paid
Created --MarkAbandoned(reason)---------> Abandoned
Created --MarkExpired(reason)-----------> Expired
Paid    --MarkRefunded()----------------> Refunded
Refunded --RevertToPaid()--------------> Paid
```

All transition methods return `bool` — `false` means the order is already in a terminal state (idempotent no-op). Callers must not throw on `false`.

Factory: `Order.Create(userId, totalAmount, currency, sagaId, idempotencyKey, customerEmail, lineItems)`. Requires at least one line item. `SagaId` has a unique index — the `CreateOrderCommandHandler` checks for existing orders by `SagaId` before creating, providing idempotency.

### OrderItem (`Orders.Domain.OrderItem`)
Owned by `Order` (cascade delete). Snapshots `ProductName` and `UnitPrice` at order time — no navigation to Catalog's `Product`. `LineTotal` is a computed property (`Quantity * UnitPrice`). Factory: `OrderItem.Create(orderId, productId, productName, quantity, unitPrice)`.

**Invariants:** `OrderId`, `ProductId` must be non-empty Guids; `Quantity > 0`; `UnitPrice >= 0`; `ProductName` non-empty.

### GuestOrderInfo (`Orders.Domain.GuestOrderInfo`)
Attached to `Order` (one-to-one) for guest checkouts. Holds shipping address and a unique `OrderToken` for lookup without authentication. `CreateStub(orderId, orderToken)` creates an incomplete record; `Complete(...)` fills in the address details.

### StockReleaseFailure
Records failed stock release attempts (compensation errors) for operations review. Owns a collection of `StockReleaseFailureItems`.

### OrderStatus (`Orders.Domain.OrderStatus`)

| Value | Meaning |
|---|---|
| `Created` (0) | Order created, awaiting events |
| `Paid` (1) | `PaymentCompletedEvent` received |
| `Abandoned` (2) | `PaymentSessionFailedEvent` or `StockReservationFailedEvent` received |
| `Expired` (3) | Stripe checkout session expired |
| `Refunded` (4) | Refund completed |

---

## API Endpoints

### Orders (`/api/orders`)

| Method | Route | Auth | Description |
|---|---|---|---|
| GET | `/api/orders/{id}` | Bearer JWT | Get order by ID |
| GET | `/api/orders/by-user/{userId}` | Bearer JWT | List orders for user; enforces `userId == authenticatedUser` unless `Admin` role |
| GET | `/api/orders/lookup` | None (anonymous) | Guest order lookup by `?token=&email=` query params |
| POST | `/api/orders` | Bearer JWT | Create order (normally called by saga / CheckoutOrchestrator, not directly by end users) |

Pagination on `by-user`: `skip` and `take` query parameters (default `skip=0`, `take=20`).

---

## Events

### Published

| Event | Trigger | Description |
|---|---|---|
| `OrderCreatedEvent` | `CreateOrderCommandHandler` | Emitted after order persisted; carries `OrderId`, `CustomerId` (Guid), `TotalAmount`, `CustomerEmail`. Skipped if `UserId` is not a valid Guid. |
| `OrderCompletedEvent` | `PaymentCompletedConsumer` | Emitted after `Order.MarkPaid`; carries `OrderId`, `CustomerId`, `TotalAmount`, `CustomerEmail`, `CompletedAt`, `PaymentId` |
| `OrderAbandonedEvent` | `StockReservationFailedConsumer`, `PaymentSessionFailedConsumer`, `CheckoutSessionExpiredConsumer` | Emitted after `Order.MarkAbandoned`/`MarkExpired`; carries `OrderId`, `SagaId`, line items, `AgeAtAbandonment`, `PreviousStatus`, `CustomerEmail` |

All events published via `IDomainEventPublisher` before `SaveChangesAsync`, committing the `OutboxMessage` in the same EF transaction as the order state change.

### Consumed

| Event | Consumer | Action |
|---|---|---|
| `PaymentCompletedEvent` | `PaymentCompletedConsumer` | Calls `Order.MarkPaid(paymentId)`; publishes `OrderCompletedEvent` |
| `PaymentSessionFailedEvent` | `PaymentSessionFailedConsumer` | Calls `Order.MarkAbandoned(reason)`; publishes `OrderAbandonedEvent` |
| `StockReservationFailedEvent` | `StockReservationFailedConsumer` | Calls `Order.MarkAbandoned(reason)`; publishes `OrderAbandonedEvent` |
| `CheckoutSessionExpiredEvent` | `CheckoutSessionExpiredConsumer` | Calls `Order.MarkExpired(reason)` |
| `RefundCompletedEvent` | `RefundCompletedConsumer` | Calls `Order.MarkRefunded()` |
| `RefundCancelledEvent` | `RefundCancelledConsumer` | Calls `Order.RevertToPaid()` |

**Idempotency layers:**
1. MassTransit inbox deduplicates by `MessageId`.
2. `Order.MarkPaid` / `MarkAbandoned` return `false` for already-terminal orders — consumer skips re-publishing.
3. EF xmin concurrency token on `Orders` catches concurrent transitions.

---

## Configuration

| Key | Description |
|---|---|
| `ConnectionStrings:OrdersDb` | PostgreSQL connection string |
| `RabbitMQ:Host` / credentials | MassTransit transport |

Authentication uses the platform-shared JWKS endpoint configured via `AddPlatformAuthentication`.

---

## Database

- **Schema:** `orders`
- **DbContext:** `OrderDbContext`
- **Migration runner:** `MigrateWithRetryAsync` on startup (skipped in `Test` environment)

### Key tables

| Table | Description |
|---|---|
| `orders.Orders` | Order aggregates; xmin concurrency token; unique index on `SagaId` |
| `orders.OrderItems` | Line items (snapshot of product name + price at order time) |
| `orders.GuestOrders` | Guest shipping info; unique index on `OrderToken` |
| `orders.StockReleaseFailures` | Failed compensation records |
| `orders.StockReleaseFailureItems` | Owned collection of failed release items |
| `orders.OutboxMessages` | MassTransit transactional outbox |
| `orders.OutboxState` | Outbox delivery state |
| `orders.InboxState` | Inbox deduplication |

### Column details (Orders table)

| Column | Type | Constraints |
|---|---|---|
| `UserId` | `varchar(450)` | Required |
| `SagaId` | `uuid` | Required, unique index |
| `IdempotencyKey` | `varchar(200)` | Required |
| `CustomerEmail` | `varchar(254)` | Required |
| `TotalAmount` | `numeric(18,2)` | Required |
| `Currency` | `varchar(3)` | Required |
| `Status` | `varchar(20)` | Required, stored as string |
| `AbandonReason` | `varchar(500)` | Nullable |
| `xmin` | `xid` | Optimistic concurrency token |

### Migrations

| Migration | Date | Description |
|---|---|---|
| `20260503205539_InitialCreate` | 2026-05-03 | Orders, OrderItems, outbox/inbox |
| `20260504034511_AddOrdersDomainExpansion` | 2026-05-04 | GuestOrders, StockReleaseFailures, additional Order fields (CustomerEmail, AbandonReason, SagaId) |

---

## Testing

### Test projects

| Project | Location | Coverage |
|---|---|---|
| `Orders.Unit` | `tests/Orders/Orders.Unit/` | Domain model (`OrderTests`), command handlers (`CreateOrderCommandHandler`), query handlers (GetOrderById, ListUserOrders, GetGuestOrder), controller (`OrdersController`) |
| `Orders.Integration` | `tests/Orders/Orders.Integration/` | HTTP flows (`OrderFlowsTests`), auth enforcement (`AuthTests`), idempotency middleware (`IdempotencyMiddlewareTests`), `CheckoutSessionExpiredConsumer` integration via `OrdersWebAppFactory` |
| `Orders.Architecture` | `tests/Orders/Orders.Architecture/` | Dependency boundary enforcement |
| `Orders.Contract` | `tests/Orders/Orders.Contract/` | Pact contract tests (`OrderEventsConsumerTests`) |

### Running tests

```bash
# Unit tests
dotnet test tests/Orders/Orders.Unit/

# Integration tests (requires Docker)
dotnet test tests/Orders/Orders.Integration/

# Architecture tests
dotnet test tests/Orders/Orders.Architecture/
```

Integration tests use `SharedTestPostgres.CreateDatabaseAsync("orders")` from `BuildingBlocks.Testing.Containers`. Raw container instantiation is prohibited by the platform architecture check.
