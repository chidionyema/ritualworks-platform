# Notifications Service

Multi-channel notification delivery (email, SMS, push) with Scriban template rendering, delivery state tracking, and provider webhook ingestion.

## Responsibilities
- Render notifications from Scriban templates
- Dispatch via SendGrid, AWS SES, Twilio, or FCM depending on channel
- Track delivery state machine: Created → Rendering → Queued → Sent → Delivered / Bounced / Failed
- Ingest provider delivery webhooks with signature validation
- Idempotent processing via EF Core idempotency middleware

## API Endpoints
| Method | Route | Notes |
|--------|-------|-------|
| POST | `/api/webhooks/ses` | AWS SES/SNS delivery events |
| POST | `/api/webhooks/sendgrid` | SendGrid events (ECDSA signature) |
| POST | `/api/webhooks/twilio` | Twilio status callbacks (HMAC) |

## Domain Entities
- **Notification** — state machine aggregate; `MarkRendering()`, `MarkQueued()`, `MarkSent()`, `MarkDelivered()`, `MarkBounced()`, `MarkFailed()`, `RecordAttempt()`

## Events Consumed
- `NotificationRequestedEvent` (`NotificationRequestConsumer`)

## Infrastructure Dependencies
- PostgreSQL (`NotificationsDbContext`) with idempotency
- RabbitMQ via MassTransit
- SendGrid, AWS SES, Twilio, FCM — credentials from Vault KV

## Configuration
```
ConnectionStrings:notifications
Vault:Enabled — maps secrets for Ses / SendGrid / Twilio / Fcm
RabbitMq:Host / Username / Password
Notifications:Providers:Ses / SendGrid / Twilio / Fcm
```

## Health Checks
- DB: `AddDbHealthCheck<NotificationsDbContext>()`
