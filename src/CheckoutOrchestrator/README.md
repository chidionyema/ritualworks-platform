# CheckoutOrchestrator Service

## Overview

The CheckoutOrchestrator service owns the cross-service checkout choreography. It is the bounded context responsible for coordinating stock reservation, payment session creation, and order completion via a persistent MassTransit saga. It does not own order or payment aggregates; those remain authoritative in their respective services. This service is the sole writer to the `checkout` schema.

The public entry point is `POST /api/checkouts`, which the BFF calls after allocating saga and order IDs. The saga then drives all subsequent steps asynchronously via RabbitMQ domain events, with real-time push back to the browser via the BFF's SignalR hub.

---

## Architecture

### Layers

| Layer | Project | Responsibility |
|---|---|---|
| Domain | `CheckoutOrchestrator.Domain` | `CheckoutSagaState` — the saga's persistent state record |
| Application | `CheckoutOrchestrator.Application` | `CheckoutSaga` state machine, `StartCheckoutCommand`, MediatR pipeline, FluentValidation |
| Infrastructure | `CheckoutOrchestrator.Infrastructure` | `CheckoutDbContext`, EF Core migrations, MassTransit wiring, `PaymentExpiryWatcher` background service |
| API | `CheckoutOrchestrator.Api` | REST controllers, Swagger, idempotency middleware |

### Key Dependencies

- **MassTransit 8.x + RabbitMQ** — saga transport, transactional outbox, in-bus delayed message scheduler (RabbitMQ delayed-message-exchange plugin)
- **EF Core 9 + PostgreSQL** — saga state persistence and EF Core outbox
- **MediatR + FluentValidation** — command handling and input validation pipeline
- **Vault** — dynamic Postgres credentials via `DynamicCredentialsConnectionInterceptor` (role: `haworks-checkout-orchestrator`; disabled in test environments)
- **OpenTelemetry** — custom `checkout.saga.compensate` activity source for compensation tracing
- **Serilog** — structured logging

---

## Domain Model

### CheckoutSagaState

The saga's only domain object. Implements `SagaStateMachineInstance` and `ISagaVersion` (MT optimistic concurrency). All fields are a snapshot of the data needed to drive orchestration — no business logic lives here (ADR-0009).

| Field | Type | Purpose |
|---|---|---|
| `CorrelationId` | `Guid` | Saga ID; the cross-service correlation key |
| `CurrentState` | `string` | MT state name (e.g., `Initiated`, `ReadyForPayment`) |
| `Version` | `int` | MassTransit OCC token |
| `OrderId` | `Guid` | The order being fulfilled |
| `UserId` | `string` | Identity of the purchasing user |
| `CustomerEmail` | `string` | For payment provider session |
| `TotalAmount` | `decimal` | Order total |
| `Currency` | `string` | Defaults to `USD` |
| `IdempotencyKey` | `string?` | Client-supplied idempotency token |
| `LineItemsJson` | `string` | JSON snapshot of cart items |
| `ReservedItemsJson` | `string?` | JSON snapshot of reserved items (for compensation) |
| `PaymentId` | `Guid?` | Set when payment session is created |
| `PaymentSessionId` | `string?` | Provider-side session ID |
| `PaymentCheckoutUrl` | `string?` | Redirect URL sent to the browser via SignalR |
| `FailureReason` | `string?` | Populated on any non-Completed terminal state |
| `PaymentExpiryTokenId` | `Guid?` | Handle for the scheduled `PaymentExpiredEvent` |
| `CreatedAt` | `DateTime` | Saga creation timestamp |

### CheckoutSaga State Machine

Implemented as `MassTransitStateMachine<CheckoutSagaState>`.

#### States

| State | Meaning |
|---|---|
| `Initial` | Not yet started |
| `Initiated` | Saga started; `StockReservationRequestedEvent` published |
| `StockReservedState` | Stock reserved; `PaymentSessionRequestedEvent` published; 15-minute payment expiry timer started |
| `ReadyForPayment` | Payment session created; waiting for customer to pay |
| `Completed` | Payment received; expiry timer cancelled (final) |
| `Abandoned` | Non-recoverable failure — stock exhausted, payment failed, or expiry (final) |
| `RequiresReview` | Payment amount mismatch; ops intervention required (quasi-final) |

#### Happy Path Transitions

```
Initial
  --[CheckoutInitiatedEvent]--> Initiated          publishes StockReservationRequestedEvent
  --[StockReservedEvent]------> StockReservedState publishes PaymentSessionRequestedEvent, schedules PaymentExpiry (15 min)
  --[PaymentSessionCreated]---> ReadyForPayment
  --[PaymentCompletedEvent]---> Completed          cancels PaymentExpiry timer
```

#### Compensation Paths

| Trigger | From State | Action |
|---|---|---|
| `StockReservationFailedEvent` | `Initiated` | Transition to `Abandoned` (no stock to release) |
| `PaymentSessionFailedEvent` | `StockReservedState` or `ReadyForPayment` | Publish `StockReleaseRequestedEvent`, transition to `Abandoned` |
| `PaymentExpiredEvent` (timer) | `StockReservedState` or `ReadyForPayment` | Publish `StockReleaseRequestedEvent` + `CheckoutSessionExpiredEvent`, transition to `Abandoned` |
| `PaymentAmountMismatchEvent` | `ReadyForPayment` | Cancel timer, transition to `RequiresReview` |

#### Scheduled Timeout

A `PaymentExpiredEvent` is scheduled 15 minutes after stock is reserved using MassTransit's in-bus delayed scheduler. The `PaymentExpiryWatcher` background service (polls every 60 s) acts as a belt-and-braces fallback in case the broker's delayed-message-exchange plugin is unavailable or drops the message.

---

## API Endpoints

| Method | Route | Auth | Description |
|---|---|---|---|
| `POST` | `/api/checkouts` | Bearer JWT (required) | Start a new checkout saga. Accepts saga ID, order ID, user details, and line items. Returns `202 Accepted` with `{sagaId, orderId}`. Idempotent via `X-Idempotency-Key`. |
| `GET` | `/api/checkouts/{sagaId}` | Bearer JWT (required) | Retrieve saga state by saga ID. Returns current state, payment URL, failure reason. |
| `GET` | `/api/checkouts/by-order/{orderId}` | Bearer JWT (required) | Retrieve saga state by order ID. |

All routes are under `[Authorize]`. The idempotency middleware (`UseIdempotency`) is scoped by `UserId` and keyed on the `X-Idempotency-Key` header.

### Request Body — POST /api/checkouts

```json
{
  "sagaId": "<guid>",
  "orderId": "<guid>",
  "userId": "<string>",
  "customerEmail": "<string>",
  "totalAmount": 0.00,
  "idempotencyKey": "<string>",
  "items": [
    { "productId": "<guid>", "productName": "<string>", "quantity": 1, "unitPrice": 0.00 }
  ]
}
```

---

## Events

### Published (via MassTransit outbox)

| Event | When |
|---|---|
| `StockReservationRequestedEvent` | On `CheckoutInitiatedEvent` — requests stock reservation from Catalog |
| `PaymentSessionRequestedEvent` | On `StockReservedEvent` — requests payment session from Payments |
| `StockReleaseRequestedEvent` | On compensation — instructs Catalog to release reserved stock |
| `CheckoutSessionExpiredEvent` | On payment expiry timeout |
| `PaymentExpiredEvent` | Scheduled by MT delayed scheduler; consumed by the saga itself |

### Consumed (via RabbitMQ)

| Event | Source | Effect |
|---|---|---|
| `CheckoutInitiatedEvent` | BFF / external | Starts the saga |
| `StockReservedEvent` | Catalog | Transitions to `StockReservedState` |
| `StockReservationFailedEvent` | Catalog | Transitions to `Abandoned` |
| `PaymentSessionCreatedEvent` | Payments | Transitions to `ReadyForPayment` |
| `PaymentSessionFailedEvent` | Payments | Triggers compensation |
| `PaymentCompletedEvent` | Payments | Transitions to `Completed` |
| `PaymentAmountMismatchEvent` | Payments | Transitions to `RequiresReview` |
| `PaymentExpiredEvent` | Self (MT scheduler) | Triggers compensation |

---

## Configuration

Configuration is injected by .NET Aspire at runtime. No secrets are stored in `appsettings.json`.

| Key | Source | Description |
|---|---|---|
| `ConnectionStrings:checkout` | Aspire `WithReference(checkoutDb)` | PostgreSQL connection string for the checkout schema |
| `ConnectionStrings:rabbitmq` | Aspire `WithReference(rabbitmq)` | RabbitMQ AMQP URI |
| `Vault:Enabled` | Environment variable | Enables Vault dynamic Postgres credentials (disabled in `Test`) |
| `Checkout:SuccessUrl` | App config / Vault secret | Redirect URL after successful payment (required, must be a valid URL) |
| `Checkout:CancelUrl` | App config / Vault secret | Redirect URL after cancelled payment (required, must be a valid URL) |

---

## Database

- **Schema**: `checkout`
- **Migration history table**: `checkout.__EFMigrationsHistory`
- **DbContext**: `CheckoutDbContext`
- **Auto-migrate**: on startup (skipped in `Test` environment)

### Key Tables

| Table | Description |
|---|---|
| `checkout.checkout_saga_states` | Saga state rows; one row per active/terminal saga instance |
| `checkout.outbox_messages` | MassTransit EF Core outbox — pending domain event publications |
| `checkout.outbox_state` | MassTransit outbox delivery state tracking |
| `checkout.inbox_state` | MassTransit inbox deduplication records |

---

## Testing

Test projects live under `tests/CheckoutOrchestrator/`.

| Project | Type | Coverage |
|---|---|---|
| `CheckoutOrchestrator.Unit` | Unit | `Validators/` — FluentValidation tests for `StartCheckoutCommand` |
| `CheckoutOrchestrator.Integration` | Integration | Full saga flows with shared Testcontainers Postgres; uses `SharedTestPostgres.CreateDatabaseAsync("checkout")` |
| `CheckoutOrchestrator.Architecture` | Architecture | NetArchTest boundary enforcement |

### Integration Test Files

| File | What it tests |
|---|---|
| `SagaFlowsTests.cs` | Happy path: `CheckoutInitiated` → `Completed` |
| `SagaCompensationChaosTests.cs` | Compensation paths: stock failure, payment failure |
| `PaymentExpiryTests.cs` | Payment expiry timer and `PaymentExpiryWatcher` fallback |
| `AuthTests.cs` | JWT authentication enforcement on all endpoints |
| `CheckoutSagaEndToEndTests.cs` | Full end-to-end saga with real MassTransit in-memory harness |

Integration tests use `CheckoutWebAppFactory` (derives from `WebApplicationFactory<Program>`) with the `Test` environment to suppress Vault, migrations, and Kafka.
