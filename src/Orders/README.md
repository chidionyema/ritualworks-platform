# Orders Service

Manages order lifecycle from creation through fulfilment, refund, and GDPR anonymisation.

## Responsibilities
- Create orders and expose read APIs (by id, by user, guest token lookup)
- Transition order state in response to saga events
- Anonymise order data on GDPR erasure request

## API Endpoints
| Method | Route | Notes |
|--------|-------|-------|
| POST | `/api/orders` | Create order |
| GET | `/api/orders/{id}` | Get by id |
| GET | `/api/orders` | List by authenticated user |
| GET | `/api/orders/guest/{token}` | Guest order lookup |

## Domain Entities
- **Order** — `MarkPaid()`, `MarkAbandoned()`, `MarkExpired()`, `MarkRefunded()`, `RevertToPaid()`, `AnonymiseForPrivacy()`

## Events Consumed
- `PaymentCompletedEvent` → marks order Paid, publishes `OrderCompletedEvent`
- `CheckoutAbandonedEvent` → marks order Abandoned
- `PaymentExpiredEvent` → marks order Expired
- `RefundCompletedEvent` → marks order Refunded
- `PrivacyErasureRequestedEvent` → anonymises PII

## Events Published
- `OrderCompletedEvent`

## Infrastructure Dependencies
- PostgreSQL (`OrdersDbContext`) with EF Core outbox
- RabbitMQ via MassTransit (7 consumers)

## Configuration
```
ConnectionStrings:orders
RabbitMq:Host / Username / Password
```

## Health Checks
- DB: `AddDbHealthCheck<OrdersDbContext>()`
