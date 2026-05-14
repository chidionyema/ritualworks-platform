# Webhooks Service

## Overview

The Webhooks service provides outbound webhook delivery for external partners. It is the bounded context responsible for subscription management, event fan-out, signed HTTP delivery with exponential-backoff retry, and delivery audit.

The service receives domain events through two independent channels:

1. **MassTransit / RabbitMQ** — for high-level domain events (`OrderCreatedEvent`, `PaymentCompletedEvent`, etc.) published by other microservices via their transactional outboxes.
2. **Kafka / Debezium CDC** — for low-level database change events (`db.catalog.public.products`, `db.orders.public.orders`, etc.) streamed by Debezium Connect.

Both channels resolve active subscriptions, persist `WebhookDelivery` records, and enqueue Hangfire jobs for durable HTTP delivery.

---

## Architecture

### Layers

| Layer | Project | Responsibility |
|---|---|---|
| Domain | `Webhooks.Domain` | `WebhookSubscription`, `WebhookDelivery`, `WebhookDeliveryAttempt` aggregates |
| Application | `Webhooks.Application` | MediatR commands/queries for subscription CRUD and delivery inspection, FluentValidation |
| Infrastructure | `Webhooks.Infrastructure` | `WebhooksDbContext`, Hangfire job storage, `WebhookDispatcher`, `EventFanOutConsumer`, `CdcFanOutWorker` |
| API | `Webhooks.Api` | REST controllers, Swagger, JWT authentication |

### Key Dependencies

- **MassTransit 8.x + RabbitMQ** — domain event consumption via `EventFanOutConsumer`
- **Kafka** — CDC event consumption via `CdcFanOutWorker` (Confluent.Kafka consumer, group ID: `webhooks-svc-cdc`)
- **Hangfire + PostgreSQL** — durable retry queue for HTTP delivery jobs (schema: `webhooks_jobs`)
- **EF Core 9 + PostgreSQL** — subscription and delivery persistence (schema: `webhooks`)
- **MediatR + FluentValidation** — command/query pipeline
- **Serilog** — structured logging

---

## Domain Model

### WebhookSubscription

Represents a partner's interest in receiving webhook calls for specific event types.

| Field | Type | Description |
|---|---|---|
| `Id` | `Guid` | Primary key |
| `PartnerId` | `Guid` | Owning partner |
| `Url` | `string` | Target endpoint URL |
| `Secret` | `string` | Plain secret stored for HMAC-SHA256 signing |
| `SecretHash` | `string` | bcrypt hash of secret for validation |
| `SecretPreview` | `string` | Truncated secret for UI display |
| `Events` | `string[]` | Array of event type strings the subscription covers |
| `IsActive` | `bool` | Soft enable/disable flag |
| `DeletedAt` | `DateTime?` | Set on soft delete |

Domain methods: `Update(url, events, isActive)`, `RotateSecret(secret, secretHash, secretPreview)`, `SoftDelete()`.

### WebhookDelivery

One delivery record per subscription per event occurrence.

| Field | Type | Description |
|---|---|---|
| `Id` | `Guid` | Primary key |
| `SubscriptionId` | `Guid` | FK to `WebhookSubscription` |
| `EventId` | `string` | Source message ID |
| `EventType` | `string` | e.g., `order.created`, `products.updated` |
| `Payload` | `string` | JSON payload sent to the partner endpoint |
| `Status` | `DeliveryStatus` | `Pending`, `Succeeded`, `Failed`, `Exhausted` |
| `Attempts` | `int` | Total attempt count |
| `NextAttemptAt` | `DateTime?` | Scheduled time for next retry |
| `FinalStatus` | `int?` | Last HTTP status code |
| `CompletedAt` | `DateTime?` | Set on `Succeeded` or `Exhausted` |

### WebhookDeliveryAttempt

Individual HTTP call record, child of `WebhookDelivery`.

| Field | Type | Description |
|---|---|---|
| `DeliveryId` | `Guid` | Parent delivery |
| `AttemptIndex` | `int` | Zero-based attempt number |
| `HttpStatus` | `int?` | HTTP response code |
| `ResponseBody` | `string?` | Truncated response body (max 8 192 chars) |
| `Error` | `string?` | Exception message on network failure |
| `Succeeded` | `bool` | Whether the attempt was considered successful |
| `DurationMs` | `int?` | Round-trip duration |

### DeliveryStatus Enum

```
Pending    - awaiting first dispatch
Succeeded  - received a 2xx response
Failed     - last attempt failed; next attempt scheduled
Exhausted  - all retry intervals exhausted; no further delivery attempts
```

---

## API Endpoints

### Subscriptions

| Method | Route | Auth | Description |
|---|---|---|---|
| `POST` | `/api/webhooks/subscriptions` | Bearer JWT | Create a subscription. Returns `201 Created` with subscription ID. |
| `GET` | `/api/webhooks/subscriptions/{id}` | Bearer JWT | Retrieve subscription by ID. |
| `PATCH` | `/api/webhooks/subscriptions/{id}` | Bearer JWT | Update URL, event list, and active flag. |
| `DELETE` | `/api/webhooks/subscriptions/{id}` | Bearer JWT | Soft-delete a subscription. |
| `POST` | `/api/webhooks/subscriptions/{id}/rotate-secret` | Bearer JWT | Rotate HMAC signing secret. Accepts optional new secret in body; generates one if omitted. |

### Deliveries

| Method | Route | Auth | Description |
|---|---|---|---|
| `GET` | `/api/webhooks/deliveries` | Bearer JWT | List deliveries. Supports query filters: `subscriptionId`, `eventType`, `status`, `skip`, `take` (default 50). |
| `GET` | `/api/webhooks/deliveries/{id}/attempts` | Bearer JWT | List all delivery attempts for a given delivery. |
| `POST` | `/api/webhooks/deliveries/{id}/replay` | Bearer JWT | Manually re-enqueue a delivery for dispatch. |

---

## Events

### Consumed via RabbitMQ (MassTransit — `EventFanOutConsumer`)

| Event | External event name | Source service |
|---|---|---|
| `OrderCreatedEvent` | `order.created` | Orders |
| `OrderCompletedEvent` | `order.completed` | Orders |
| `OrderAbandonedEvent` | `order.abandoned` | Orders |
| `PaymentCompletedEvent` | `payment.completed` | Payments |
| `RefundIssuedEvent` | `refund.issued` | Payments |

### Consumed via Kafka CDC (`CdcFanOutWorker`, group: `webhooks-svc-cdc`)

| Kafka Topic | Mapped external event |
|---|---|
| `db.catalog.public.products` | `products.created`, `products.updated`, `products.deleted` |
| `db.catalog.public.product_categories` | `product_categories.created`, `product_categories.updated`, `product_categories.deleted` |
| `db.orders.public.orders` | `orders.created`, `orders.updated`, `orders.deleted` |
| `db.payments.public.payments` | `payments.created`, `payments.updated`, `payments.deleted` |

CDC operation codes are mapped: `c` → `created`, `u` → `updated`, `d` → `deleted`.

### Published

This service does not publish domain events to RabbitMQ. All output is outbound HTTP webhook delivery.

---

## Delivery Mechanism

The `WebhookDispatcher` (Hangfire job) handles each `WebhookDelivery`:

1. Loads the delivery and the associated subscription.
2. Constructs an HTTP POST with three standard headers:
   - `Webhook-Id` — delivery ID
   - `Webhook-Timestamp` — Unix epoch seconds
   - `Webhook-Signature` — `t={timestamp},v1={HMAC-SHA256-hex}` over `{timestamp}.{payload}`
3. Sends the request and records a `WebhookDeliveryAttempt`.
4. On failure, schedules the next attempt via Hangfire using an exponential backoff schedule: 1, 2, 4, 8, 16, 32, 60, 120, 240, 480, 960, 1440 minutes (capped at 1 440 min / 24 h for the final intervals).
5. After the retry schedule is exhausted, marks the delivery as `Exhausted`.

---

## Configuration

| Key | Source | Description |
|---|---|---|
| `ConnectionStrings:webhooks` | Aspire `WithReference(webhooksDb)` | PostgreSQL connection string for the webhooks schema |
| `ConnectionStrings:rabbitmq` | Aspire `WithReference(rabbitmq)` | RabbitMQ AMQP URI |
| `ConnectionStrings:kafka` | Aspire `WithReference(kafka)` | Kafka bootstrap servers (Aspire Kafka component) |

The Kafka consumer and `CdcFanOutWorker` are disabled in the `Test` environment.

---

## Database

- **Schema**: `webhooks`
- **Hangfire schema**: `webhooks_jobs`
- **Migration history table**: `webhooks.__EFMigrationsHistory`
- **DbContext**: `WebhooksDbContext`
- **Auto-migrate**: in Development only (on startup)

### Key Tables

| Table | Description |
|---|---|
| `webhooks.webhook_subscriptions` | Partner subscription records |
| `webhooks.webhook_deliveries` | One row per event-subscription delivery |
| `webhooks.webhook_delivery_attempts` | Individual HTTP call records |
| `webhooks_jobs.*` | Hangfire job tables (job queue, retry state) |

---

## Testing

Test projects live under `tests/Webhooks/`.

| Project | Type | Coverage |
|---|---|---|
| `Webhooks.Unit` | Unit | Domain model tests (`Domain/`), application handler tests (`Application/`), infrastructure unit tests (`Infrastructure/`) |
| `Webhooks.Integration` | Integration | Full subscription and delivery flows; uses `SharedTestPostgres.CreateDatabaseAsync("webhooks")` |

### Integration Test Files

| File | What it tests |
|---|---|
| `SubscriptionIntegrationTests.cs` | CRUD operations on subscriptions, secret rotation, soft delete, fan-out delivery creation |

Integration tests use `WebhooksWebAppFactory` with the `Test` environment, which suppresses the Kafka consumer and `CdcFanOutWorker`. The MassTransit `EventFanOutConsumer` is tested by injecting events into the MassTransit in-memory harness.
