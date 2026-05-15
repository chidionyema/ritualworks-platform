# Payments Service

Multi-provider payment processing (Stripe + PayPal) with refund and subscription saga orchestration.

## Responsibilities
- Process checkout sessions, subscriptions, and refunds via Stripe or PayPal
- Run `RefundSaga` and `SubscriptionSaga` MassTransit state machines
- Monitor refund timeouts and subscription renewal via hosted watchers
- Route provider webhooks with idempotency guard
- Dynamic Postgres credentials via Vault `DynamicCredentialsConnectionInterceptor`
- Hybrid L1/L2 caching for payment session data

## API Endpoints
| Method | Route | Notes |
|--------|-------|-------|
| POST | `/api/refunds` | Initiate refund |
| GET | `/api/refunds/{id}` | Refund status |
| GET | `/api/subscriptions/status` | Subscription status |
| POST | `/api/subscriptions/create-checkout-session` | |
| POST | `/api/subscriptions/cancel` | |
| POST | `/api/subscriptions/resume` | |
| POST | `/api/webhooks/stripe` | Stripe webhook |
| POST | `/api/webhooks/paypal` | PayPal webhook |

## Domain Entities
- **Payment**, **RefundSagaState**, **SubscriptionSagaState**

## Events Consumed
- `PaymentSessionRequestedEvent`, `PaymentWebhookValidatedEvent`
- `ProviderRefundInitiationRequestedEvent`, `ProviderRefundCancellationEvent`
- `SubscriptionRenewalRequestedEvent`, `PrivacyErasureRequestedEvent`

## Events Published
- `PaymentCompletedEvent`, `PaymentFailedEvent`
- `RefundCompletedEvent`, `RefundFailedEvent`

## Infrastructure Dependencies
- PostgreSQL (`PaymentDbContext`) with Vault dynamic credentials
- RabbitMQ via MassTransit (delayed scheduler + outbox)
- Stripe SDK, PayPal SDK
- HybridCache

## Configuration
```
ConnectionStrings:payments / rabbitmq
Vault:Enabled / RoleId / SecretId
Payments:ActiveProvider (Stripe | PayPal)
Payments:Stripe:SecretKey / WebhookSecret
Payments:PayPal:ClientId / ClientSecret / WebhookId
```

## Health Checks
- `PaymentProviderHealthCheck` (verifies active provider connectivity)
