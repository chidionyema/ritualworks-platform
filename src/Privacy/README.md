# Privacy Service

GDPR erasure coordination. Runs `PrivacyRequestStateMachine` to fan out erasure commands to Identity, Orders, and Payments, then confirms completion.

## Responsibilities
- Accept GDPR erasure requests and track state across three downstream services
- Run `PrivacyRequestStateMachine`: Processing → Completed / Failed / Stalled (7-day timeout)
- Track partial completion flags: `IdentityCompleted`, `OrdersCompleted`, `PaymentsCompleted`

## Saga States
`Processing` → `Completed` | `Failed` | `Stalled`

## Saga State Fields
`CorrelationId`, `UserId`, `RequestedAt`, `IdentityCompleted`, `OrdersCompleted`, `PaymentsCompleted`

## Events Consumed
- `ErasureRequestedEvent` (initiates saga)
- `IdentityErasureCompletedEvent`
- `OrdersErasureCompletedEvent`
- `PaymentsErasureCompletedEvent`

## Events Published
- `PrivacyErasureRequestedEvent` → Identity, Orders, Payments
- `ErasureCompletedEvent` (when all three confirm)

## Infrastructure Dependencies
- PostgreSQL (`PrivacyDbContext`) for saga state persistence
- RabbitMQ via MassTransit

## Configuration
```
ConnectionStrings:privacy
RabbitMq:Host / Username / Password
```

## Health Checks
- DB: `AddDbHealthCheck<PrivacyDbContext>()`
