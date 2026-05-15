# Webhooks Service

Outbound webhook delivery platform for external partners. Fans out platform domain events to partner-registered HTTP endpoints with HMAC signing and Hangfire-backed retries.

## Responsibilities
- Fan out 5 domain event types to matching `WebhookSubscription` records (`EventFanOutConsumer<T>`)
- Enqueue Hangfire delivery jobs per matching subscription
- Sign payloads with HMAC-SHA256 using per-subscription secret
- Expose partner API for CRUD on subscriptions
- Rotate subscription secrets (`WebhookSubscription.RotateSecret()`)

## API Endpoints
| Method | Route | Notes |
|--------|-------|-------|
| GET | `/api/webhooks/subscriptions` | List partner subscriptions |
| POST | `/api/webhooks/subscriptions` | Create subscription |
| PUT | `/api/webhooks/subscriptions/{id}` | Update subscription |
| DELETE | `/api/webhooks/subscriptions/{id}` | Soft-delete |
| POST | `/api/webhooks/subscriptions/{id}/rotate-secret` | Rotate HMAC secret |

## Domain Entities
- **WebhookSubscription** — `PartnerId`, `Url`, `Secret`, `SecretHash`, `SecretPreview`, `Events[]`; `RotateSecret()`, `SoftDelete()`
- **WebhookDelivery** — delivery attempt record with status and response body

## Events Consumed (fan-out)
- `OrderCompletedEvent`, `PaymentCompletedEvent`, `PaymentFailedEvent`
- `RefundCompletedEvent`, `ContentAvailableEvent`

## Infrastructure Dependencies
- PostgreSQL (`WebhooksDbContext`) with EF Core outbox
- RabbitMQ via MassTransit (5 fan-out consumers)
- Hangfire with PostgreSQL storage (delivery job queue)
- HTTP client (outbound delivery to partner URLs)

## Configuration
```
ConnectionStrings:webhooks
RabbitMq:Host / Username / Password
Hangfire:Workers
```

## Health Checks
- DB: `AddDbHealthCheck<WebhooksDbContext>()`
