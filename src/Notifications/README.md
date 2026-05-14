# Notifications Service

## Overview

The Notifications service is the bounded context responsible for delivering messages to users across three channels: email, SMS, and push. It handles the full delivery lifecycle — from initial command intake, through preference and suppression gating, template rendering, provider dispatch, provider webhook ingestion, and final status updates on the aggregate.

The service implements multi-provider failover: email has two providers (AWS SES and SendGrid), SMS uses Twilio, and push uses Firebase Cloud Messaging (FCM). Provider credentials are loaded from HashiCorp Vault at startup in production.

Bounded context: **Notifications** — the service treats user identifiers, order IDs, and refund IDs as opaque cross-context references. It does not own or query identity, order, or payment data directly.

---

## Architecture

| Layer | Project | Responsibility |
|---|---|---|
| Domain | `Notifications.Domain` | `Notification` aggregate, `NotificationTemplate`, `NotificationPreference`, `Suppression`, `RateLimitBucket` entities, domain events, `DeliveryAttempt` value object, enums |
| Application | `Notifications.Application` | `SendNotificationCommand`, MassTransit consumers, template rendering (Scriban), preference service, suppression service, idempotency key generation, webhook event handling |
| Infrastructure | `Notifications.Infrastructure` | Channel gateways (email/SMS/push), provider implementations (SES, SendGrid, Twilio, FCM), EF Core `NotificationsDbContext`, MassTransit outbox wiring, Vault bootstrap |
| API | `Notifications.Api` | `NotificationsController`, `WebhooksController`, JWT authentication, idempotency middleware |

**Key dependencies:**
- **MediatR** — `SendNotificationCommand` dispatch
- **MassTransit 8 + RabbitMQ** — `NotificationRequestConsumer`, `RefundEmailConsumer`, `NotificationWebhookValidatedConsumer`; transactional outbox backed by EF Core
- **Scriban** — template rendering engine
- **AWS SDK (SES)** — email delivery via Amazon SES
- **SendGrid** — email delivery failover
- **Twilio** — SMS delivery and webhook signature validation
- **Firebase Admin SDK (FCM)** — push notification delivery
- **EF Core 9 + Npgsql** — persistence
- **HashiCorp Vault** — runtime secret injection for all four provider credential sets
- **`AddPostgresIdempotency`** — platform idempotency middleware backed by Postgres

---

## Domain Model

### Aggregate: `Notification`

The central aggregate. Created via `Notification.Create(...)` (enforces non-null `recipient`, `templateId`, `idempotencyKey`).

**Properties:**

| Property | Type | Description |
|---|---|---|
| `Id` | `Guid` | Surrogate primary key |
| `UserId` | `string?` | Opaque identity-svc user ID; null for anonymous flows (e.g. guest order emails) |
| `Recipient` | `string` | Channel-appropriate address: email address, E.164 phone number, or FCM push token |
| `Channel` | `NotificationChannel` | `Email`, `Sms`, or `Push` |
| `TemplateId` | `string` | Template identifier resolved by `ITemplateSelector` |
| `Status` | `NotificationStatus` | Current lifecycle state (see below) |
| `Priority` | `NotificationPriority` | `Low`, `Normal`, or `High` |
| `Subject` | `string` | Rendered subject line |
| `Body` | `string` | Rendered body |
| `IdempotencyKey` | `string` | SHA-256 hash; deduplicated at the repository layer |
| `ProviderMessageId` | `string?` | Provider's correlation ID (set by the channel gateway on `MarkSent`) |
| `SentAt` | `DateTime?` | Timestamp of provider acceptance |
| `DeliveredAt` | `DateTime?` | Timestamp of provider delivery confirmation (from webhook) |
| `ErrorMessage` | `string?` | Failure or bounce reason |

**Navigation:** `DeliveryAttempts` (`DeliveryAttempt[]`) — owned collection, one entry per provider call attempt.

### Enum: `NotificationStatus`

```
Created -> Rendering -> Queued -> Sent -> Delivered
                                       -> Bounced      (terminal; hard bounce; recipient is suppressed)
                                       -> Complained   (terminal; spam report)
                 Any non-terminal -> Failed            (all providers exhausted)
                 Any gate failure -> Suppressed        (preference gate / suppression list)
```

Terminal states: `Delivered`, `Bounced`, `Complained`, `Failed`, `Suppressed`. Terminal states cannot be re-entered.

### Value Object: `DeliveryAttempt`

```csharp
record DeliveryAttempt(
    DateTime AttemptedAt,
    string ProviderName,
    string? ProviderMessageId,
    bool IsSuccess,
    string? ErrorMessage)
```

One record is appended to the aggregate for every provider call, including failed attempts and failover retries.

### Entity: `NotificationTemplate`

| Property | Description |
|---|---|
| `TemplateId` | Unique template identifier |
| `Name` | Human-readable name |
| `Category` | Notification category (maps to preference category) |
| `Channel` | Target channel string |
| `Locale` | Locale code (e.g. `en-US`) |
| `SubjectTemplate` | Scriban template string for subject |
| `BodyTemplate` | Scriban template string for body |
| `TextFallbackTemplate` | Plain-text fallback for email |
| `IsActive` | Soft enable/disable |
| `Version` | Template version |
| `RequiredVariablesJson` | JSON array of required variable names |

### Entity: `NotificationPreference`

Per-user, per-category, per-channel opt-in/opt-out. Includes `QuietHoursJson` for time-window suppression.

### Entity: `Suppression`

Suppression list entry. Keyed by `RecipientHash` (hashed for PII reduction) and `Channel`. Created automatically on `Bounced` or `Complained` transitions.

### Entity: `RateLimitBucket`

Sliding-window rate limit tracker per bucket key.

---

## API Endpoints

### `NotificationsController` — `/api/notifications`

| Method | Route | Auth | Description |
|---|---|---|---|
| `POST` | `/api/notifications` | JWT | Send a notification. Enforces preference gating, suppression checks, and idempotency. On success, creates a `Notification` in `Created` state and publishes `NotificationCreatedEvent` in the same EF transaction (outbox). Returns the notification ID. |
| `GET` | `/api/notifications/{id}` | JWT | Retrieve notification status and metadata by ID. |

**`POST /api/notifications` request body (`SendNotificationCommand`):**

| Field | Type | Description |
|---|---|---|
| `userId` | `string?` | Opaque user ID; null for anonymous flows |
| `recipient` | `string` | Delivery address |
| `channel` | `enum` | `Email`, `Sms`, or `Push` |
| `templateId` | `string` | Template to render |
| `priority` | `enum` | `Low`, `Normal`, or `High` |
| `variables` | `object` | Key-value pairs passed to the Scriban renderer |
| `idempotencyKey` | `string?` | Optional caller-provided key; combined with userId + templateId + recipient and SHA-256 hashed |

### `WebhooksController` — `/api/notifications/webhooks`

Receives delivery status callbacks from email/SMS providers. Validates provider-specific signatures and publishes `NotificationWebhookValidatedEvent` via MassTransit for processing.

| Method | Route | Auth | Description |
|---|---|---|---|
| `POST` | `/api/notifications/webhooks/ses` | None (SNS signature) | AWS SES delivery events via SNS. Handles `SubscriptionConfirmation` and `Notification` types. |
| `POST` | `/api/notifications/webhooks/sendgrid` | None (ECDSA signature) | SendGrid event webhooks. Processes arrays of events; deduplicated by `sg_message_id`. |
| `POST` | `/api/notifications/webhooks/twilio` | None (Twilio signature) | Twilio SMS status callbacks. Validates `X-Twilio-Signature` using `RequestValidator`. |

Webhook events are published with a deterministic `MessageId` derived from `SHA256(provider + ":" + providerEventId)` to prevent duplicate processing.

---

## Events

### Consumed (via MassTransit / RabbitMQ)

| Event | Contract / Source | Consumer | Description |
|---|---|---|---|
| `NotificationCreatedEvent` | `Notifications.Application` (outbox) | `NotificationRequestConsumer` | Drives the aggregate through `Rendering -> Queued -> Sent`. Selects template, renders via Scriban, and dispatches via the appropriate channel gateway. |
| `NotificationWebhookValidatedEvent` | `Notifications.Application` | `NotificationWebhookValidatedConsumer` | Updates the `Notification` aggregate status based on a validated provider delivery event (Delivered, Bounced, Complained). |
| `RefundCompletedEvent` | `Haworks.Contracts.Payments` | `RefundEmailConsumer` | Sends a `refund-completed` email notification. |
| `RefundFailedEvent` | `Haworks.Contracts.Payments` | `RefundEmailConsumer` | Sends a `refund-failed` email notification. |
| `RefundStalledEvent` | `Haworks.Contracts.Payments` | `RefundEmailConsumer` | Logs an operator warning (no customer notification). |

### Published (via MassTransit outbox)

| Event | Contract | Trigger |
|---|---|---|
| `NotificationCreatedEvent` | `Notifications.Application` | Published by `SendNotificationCommandHandler` in the same EF transaction as the `Notification` INSERT. Drives `NotificationRequestConsumer`. |

---

## Configuration

Provider credentials are loaded from Vault KV paths at startup (when `Vault:Enabled=true`). The KV paths are mapped to the configuration sections listed below.

### AWS SES (`SesOptions`) — `Notifications:Providers:Ses`

| Key | Required | Description |
|---|---|---|
| `Notifications:Providers:Ses:AccessKey` | Yes | AWS access key ID |
| `Notifications:Providers:Ses:SecretKey` | Yes | AWS secret access key |
| `Notifications:Providers:Ses:Region` | Yes | AWS region (e.g. `eu-west-1`) |
| `Notifications:Providers:Ses:FromAddress` | Yes | Verified sender email address |

Vault KV path: `notifications/providers/aws-ses`

### SendGrid (`SendGridOptions`) — `Notifications:Providers:SendGrid`

| Key | Required | Description |
|---|---|---|
| `Notifications:Providers:SendGrid:ApiKey` | Yes | SendGrid API key |
| `Notifications:Providers:SendGrid:FromAddress` | Yes | Verified sender email address |

Vault KV path: `notifications/providers/sendgrid`

### Twilio (`TwilioOptions`) — `Notifications:Providers:Twilio`

| Key | Required | Description |
|---|---|---|
| `Notifications:Providers:Twilio:AccountSid` | Yes | Twilio Account SID |
| `Notifications:Providers:Twilio:AuthToken` | Yes | Twilio Auth Token (also used for webhook signature validation) |
| `Notifications:Providers:Twilio:FromNumber` | Yes | Twilio sending phone number (E.164 format) |

Vault KV path: `notifications/providers/twilio`

### FCM (`FcmOptions`) — `Notifications:Providers:Fcm`

| Key | Required | Description |
|---|---|---|
| `Notifications:Providers:Fcm:ProjectId` | Yes | Firebase project ID |
| `Notifications:Providers:Fcm:ServiceAccountJson` | Yes | Full service account JSON credential |

Vault KV path: `notifications/providers/fcm`

### Webhooks (`WebhookOptions`) — `Notifications:Webhooks`

| Key | Description |
|---|---|
| `Notifications:Webhooks:Twilio:AuthToken` | Auth token used by `WebhooksController` to validate Twilio signature (mirrors `TwilioOptions:AuthToken`) |

### Connection strings

| Key | Description |
|---|---|
| `ConnectionStrings:Notifications` | PostgreSQL connection string for the Notifications database |
| `ConnectionStrings:RabbitMQ` | RabbitMQ AMQP URI |

### Vault bootstrap

| Key | Default | Description |
|---|---|---|
| `Vault:Enabled` | `false` | Enable Vault secret loading at startup |
| `Vault:Address` | — | Vault server URL |
| `Vault:RoleId` / `Vault:SecretId` | — | AppRole credentials |

---

## Database

**Schema:** `notifications`

**Tables:**

| Table | Description |
|---|---|
| `Notifications` | Main aggregate table. `IdempotencyKey` has a unique index for deduplification. `Channel` and `Status` stored as integers. |
| `DeliveryAttempts` | Owned collection; one row per provider attempt. |
| `NotificationTemplates` | Template store. Unique index on `(TemplateId, Channel, Locale, Version)`. |
| `NotificationPreferences` | Per-user, per-category, per-channel preferences. |
| `Suppressions` | Suppression list keyed by `(RecipientHash, Channel)`. |
| `RateLimitBuckets` | Rate limit state. |
| `OutboxMessage` / `InboxState` | MassTransit EF Core transactional outbox/inbox tables. |
| `__EFMigrationsHistory` | EF Core migration tracking (schema: `notifications`). |

EF migrations are applied at startup via `MigrateWithRetryAsync` (skipped in `Test` environment).

---

## Testing

### Test projects

| Project | Path | Description |
|---|---|---|
| `Notifications.Unit` | `tests/Notifications/Notifications.Unit` | Unit tests for domain aggregate state machine, `SendNotificationCommandHandler`, preference service, suppression service, idempotency key generation |
| `Notifications.Integration` | `tests/Notifications/Notifications.Integration` | Integration tests against real Postgres; MassTransit test harness |

### Running tests

```bash
# Unit tests (no external dependencies)
dotnet test tests/Notifications/Notifications.Unit

# Integration tests (requires Docker)
dotnet test tests/Notifications/Notifications.Integration

# All Notifications tests
dotnet test tests/Notifications/
```

### Integration test infrastructure

Integration tests use the shared Testcontainers singleton:

```csharp
var db = await SharedTestPostgres.CreateDatabaseAsync("notifications");
```

MassTransit consumers are registered via `AddMassTransitTestHarness`. Provider channel gateways are replaced with stub implementations registered via `DependencyInjection.Stubs.cs` in the `Test` environment, preventing real SES/SendGrid/Twilio/FCM calls during tests.
