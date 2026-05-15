# CheckoutOrchestrator Service

Hosts the `CheckoutSaga` MassTransit state machine that coordinates stock reservation, payment session creation, and order completion.

## Responsibilities
- Drive the checkout flow: Initial → Initiated → StockReserved → ReadyForPayment → Completed / Abandoned / RequiresReview
- Schedule a 15-minute payment expiry timer (`PaymentExpirySchedule`)
- Persist saga state in PostgreSQL via EF Core outbox

## Saga States
`Initial` → `Initiated` → `StockReserved` → `ReadyForPayment` → `Completed` | `Abandoned` | `RequiresReview`

## Saga State Fields
`CorrelationId`, `CurrentState`, `OrderId`, `UserId`, `TotalAmount`, `LineItemsJson`, `ReservedItemsJson`, `PaymentId`, `PaymentExpiryTokenId`, `FailureReason`

## Events Consumed
- `CheckoutInitiatedCommand`
- `StockReservedEvent`, `StockReservationFailedEvent`
- `PaymentSessionCreatedEvent`, `PaymentCompletedEvent`
- `PaymentExpiredEvent`

## Events Published
- `StockReservationRequestedEvent`
- `PaymentSessionRequestedEvent`
- `StockReleaseRequestedEvent`
- `OrderCompletedEvent` (via outbox)

## Infrastructure Dependencies
- PostgreSQL (saga state + EF Core outbox)
- RabbitMQ via MassTransit with delayed message scheduler

## Configuration
```
ConnectionStrings:checkout_orchestrator
RabbitMq:Host / Username / Password
```

## Health Checks
- DB health check via `AddDbHealthCheck`
