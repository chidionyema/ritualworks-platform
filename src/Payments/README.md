# Payments Service

## Overview

The Payments service owns the payment lifecycle: checkout session creation, provider webhook ingestion, refunds, and subscription management. It integrates with Stripe and PayPal as pluggable providers behind a common `IPaymentGateway` / `ICheckoutSessionService` abstraction. Two MassTransit sagas live here: `RefundSaga` (orchestrates provider refund with 24-hour timeout and dunning) and `SubscriptionSaga` (manages renewal scheduling and payment recovery).

Bounded context: **Payments**. No other service writes to the `payments` schema. `OrderId` and `UserId` are opaque foreign keys; the service publishes `PaymentCompletedEvent` and `PaymentSessionFailedEvent` for orders-svc and the checkout saga to consume.

---

## Architecture

Clean Architecture with four projects:

| Layer | Project | Responsibility |
|---|---|---|
| Domain | `Payments.Domain` | `Payment`, `Subscription`, `SubscriptionPlan`, `WebhookEvent`, `RefundSagaState`, `SubscriptionSagaState` entities; `PaymentStatus`, `SubscriptionStatus`, `RefundFailureCategory` enums; repository interface |
| Application | `Payments.Application` | MassTransit consumers, `RefundSaga`, `SubscriptionSaga`, command/query handlers, application interfaces, FluentValidation validators, idempotency key generator, webhook amount-mismatch handler |
| Infrastructure | `Payments.Infrastructure` | `PaymentDbContext`, Stripe implementation (checkout, refund, subscription, webhook processor, payment session cache), PayPal implementation (checkout, refund, subscription manager, webhook processor), `WebhookRouter`, `WebhookIdempotencyGuard`, `RefundTimeoutWatcher` background service, `PaymentProviderHealthCheck` |
| API | `Payments.Api` | `WebhooksController`, `RefundsController`, `SubscriptionsController`, `AdminController`, Stripe signature validator, Vault bootstrap |

**Key dependencies:**
- MediatR (CQRS dispatch)
- MassTransit 8.x + RabbitMQ (transactional outbox, inbox deduplication, saga state machines)
- EF Core 9
- Stripe.net SDK
- PayPal REST SDK (via `IPayPalClientFactory`)
- `Haworks.BuildingBlocks.Authentication` — platform JWT validation
- `Haworks.BuildingBlocks.Idempotency` — `X-Idempotency-Key` middleware
- Vault (`VaultConfigBootstrap`) — loads `payments/stripe` and `payments/paypal` into `IConfiguration` under `PaymentProviders:Stripe` / `PaymentProviders:PayPal`
- `RefundTimeoutWatcher` — background service watching for 24-hour refund timeouts
- `PaymentProviderHealthCheck` — health check for provider connectivity
- OpenTelemetry tracing via `PaymentsActivities` source

---

## Domain Model

### Payment (`Payments.Domain.Payment`)
Aggregate root. Tracks a single payment transaction from session creation through provider webhook confirmation.

Key properties: `OrderId` (opaque FK to orders-svc), `UserId` (opaque FK to identity-svc), `SagaId` (checkout saga correlation), `Amount`, `Tax`, `Currency`, `Status`, `Provider`, `ProviderSessionId`, `ProviderCheckoutUrl`, `ProviderTransactionId`, `IsComplete`, `PaymentMethod`.

**State machine:**

```
Pending --AttachProviderSession()--> Processing
Processing --MarkCompleted()-------> Completed
Processing --MarkFailed()----------> Failed
Any --Flag()-----------------------> Flagged   (amount mismatch; manual review)
Completed --MarkRefunded()---------> Refunded
Any --MarkCancelled()--------------> Cancelled
```

Factory: `Payment.Create(orderId, userId, amount, tax, currency, provider, sagaId)`. Rejects negative amounts/tax, empty Guids, and `PaymentProvider.None`.

**PaymentStatus enum:** `Pending`, `Processing`, `Completed`, `Failed`, `Refunded`, `Cancelled`, `Flagged`.

### Subscription (`Payments.Domain.Subscription`)
Tracks a recurring subscription with a provider-specific subscription ID. Properties: `UserId`, `Provider`, `ProviderSubscriptionId`, `PlanId`, `Status`, `StartsAt`, `ExpiresAt`, `CanceledAt`.

`IsActive` computed: `Status == Active && DateTime.UtcNow < ExpiresAt`.

**SubscriptionStatus enum:** `Active`, `Canceled`, `Incomplete`, `Unknown`, `PastDue`, `Trialing`, `Expired`, `Unpaid`.

### SubscriptionPlan (`Payments.Domain.SubscriptionPlan`)
Catalog of subscription plans: `Name`, `InternalPlanId`, `Price`, `Description`, `ProviderPriceIds` (JSON mapping provider → price ID).

### WebhookEvent (`Payments.Domain.WebhookEvent`)
Idempotency record for processed provider webhook events. Unique index on `(Provider, ProviderEventId)`.

### RefundSagaState (`Payments.Domain.RefundSagaState`)
MassTransit saga state for `RefundSaga`. Correlation: `CorrelationId == RefundId`. Tracks `OrderId`, `PaymentId`, `Amount`, `Currency`, `Reason`, `Provider`, `ProviderRefundId`, `FailureDetail`, `FailureCategory`, `RefundTimeoutTokenId`.

States: `Requested`, `AwaitingProviderConfirmation`, `RequiresReview`, `Completed`, `Cancelled`.

### SubscriptionSagaState (`Payments.Domain.SubscriptionSagaState`)
MassTransit saga state for `SubscriptionSaga`. Tracks `ProviderSubscriptionId`, `UserId`, `PlanId`, `PeriodEnd`, `Amount`, `Currency`, `RenewalTimeoutTokenId`, `DunningRetryTokenId`.

### RefundFailureCategory
Enum: `ProviderRefundFailed`, `Timeout`, `OperatorCancelled`, and others.

---

## API Endpoints

### Webhooks (`/webhooks`)

| Method | Route | Auth | Description |
|---|---|---|---|
| POST | `/webhooks/stripe` | None (signature validated) | Ingests Stripe webhook; validates HMAC signature inline; publishes `PaymentWebhookValidatedEvent` with deterministic `MessageId` for inbox dedup |
| POST | `/webhooks/paypal` | None (headers captured) | Ingests PayPal webhook; bundles signature headers; publishes `PaymentWebhookValidatedEvent` for async verification by consumer |

Webhook processing is intentionally narrow: validate signature, publish event, return 200. Business processing is async via `PaymentWebhookValidatedConsumer`.

### Refunds (`/api/refunds`)

| Method | Route | Auth | Description |
|---|---|---|---|
| POST | `/api/refunds` | Bearer JWT | Initiate refund; creates `RefundRequested` event, starts `RefundSaga`; body: `{paymentId, amount, currency, reason?, requestedBy?}` |
| GET | `/api/refunds/{id}` | Bearer JWT | Get refund saga state by refund ID |

### Subscriptions (`/api/subscriptions`)

| Method | Route | Auth | Description |
|---|---|---|---|
| GET | `/api/subscriptions/status` | Bearer JWT | Get subscription status for authenticated user |
| POST | `/api/subscriptions/create-checkout-session` | Bearer JWT | Create Stripe subscription checkout session; body: `{priceId, amount, redirectPath?}` |
| POST | `/api/subscriptions/cancel` | Bearer JWT | Cancel subscription; body: `{subscriptionId, immediate}` |
| POST | `/api/subscriptions/resume` | Bearer JWT | Resume a cancelled subscription; body: `{subscriptionId}` |

---

## Events

### Published

| Event | Trigger | Description |
|---|---|---|
| `PaymentSessionCreatedEvent` | `PaymentSessionRequestedConsumer` | Session created at provider; carries `OrderId`, `SagaId`, `PaymentId`, `SessionId`, `CheckoutUrl`, `Provider`, `Amount`, `Currency` |
| `PaymentCompletedEvent` | `PaymentSessionRequestedConsumer` (demo mode) or `PaymentWebhookValidatedConsumer` (production) | Payment confirmed; carries `PaymentId`, `OrderId`, `SagaId`, `Amount`, `Currency`, `Provider`, `TransactionReference` |
| `PaymentSessionFailedEvent` | `PaymentSessionRequestedConsumer` (error / demo failure scenario) | Session creation failed; carries `OrderId`, `SagaId`, `Provider`, `ErrorCode`, `ErrorMessage`, `IsFinalAttempt` |
| `PaymentWebhookValidatedEvent` | `WebhooksController` | Raw webhook accepted and signature verified; carries `Provider`, `ProviderEventId`, `EventType`, `RawPayload`, `Signature` |
| `ProviderRefundInitiationRequestedEvent` | `RefundSaga` (`Initially` block) | Triggers `ProviderRefundInitiationRequestedConsumer` to call provider refund API |
| `RefundFailedEvent` | `RefundSaga` (failure transitions) | Refund could not be completed |
| `RefundTimedOutEvent` | `RefundSaga` (scheduled, 24-hour timeout) | Refund confirmation not received within SLA |

### Consumed

| Event | Consumer / Saga | Action |
|---|---|---|
| `PaymentSessionRequestedEvent` | `PaymentSessionRequestedConsumer` | Create `Payment` aggregate, call `ICheckoutSessionService`, publish `PaymentSessionCreatedEvent`; demo mode: simulate success/failure based on `IdempotencyKey` |
| `PaymentWebhookValidatedEvent` | `PaymentWebhookValidatedConsumer` | Route to provider-specific `IWebhookProcessor`; idempotency via `IWebhookIdempotencyGuard` |
| `ProviderRefundInitiationRequestedEvent` | `ProviderRefundInitiationRequestedConsumer` | Call provider refund API; publish `ProviderRefundInitiated` or `ProviderRefundFailed` |
| `SubscriptionRenewalRequestedEvent` | `SubscriptionRenewalRequestedConsumer` | Trigger subscription renewal at provider |
| `RefundRequested` | `RefundSaga` | Initialize saga state; publish `ProviderRefundInitiationRequestedEvent`; schedule 24-hour timeout |
| `ProviderRefundInitiated` | `RefundSaga` | Transition to `AwaitingProviderConfirmation`; record `ProviderRefundId` |
| `ProviderRefundSucceeded` | `RefundSaga` | Transition to `Completed` |
| `ProviderRefundFailed` | `RefundSaga` | Transition to `RequiresReview`; publish `RefundFailedEvent` |
| `RefundCancelledByOperator` | `RefundSaga` | Cancel saga |
| `SubscriptionStarted` | `SubscriptionSaga` | Initialize saga; schedule renewal |
| `SubscriptionRenewed` | `SubscriptionSaga` | Update period end; reschedule renewal |
| `RenewalFailed` | `SubscriptionSaga` | Begin dunning retry schedule |
| `PaymentRecovered` | `SubscriptionSaga` | Clear dunning; resume active state |
| `SubscriptionCancelled` | `SubscriptionSaga` | Finalize saga |

**Webhook idempotency:** `WebhooksController` sets `MessageId = SHA256(provider:providerEventId)[0..16]` as a deterministic Guid so MassTransit inbox deduplicates Stripe/PayPal replay deliveries.

---

## Configuration

### Required settings

| Key | Source | Description |
|---|---|---|
| `ConnectionStrings:PaymentsDb` | appsettings / Vault dynamic creds | PostgreSQL connection string |
| `PaymentProviders:Stripe:SecretKey` | Vault `payments/stripe` | Stripe secret API key (`sk_...`) |
| `PaymentProviders:Stripe:PublishableKey` | Vault `payments/stripe` | Stripe publishable key (`pk_...`) |
| `PaymentProviders:Stripe:WebhookSecret` | Vault `payments/stripe` | Stripe webhook signing secret (`whsec_...`) |
| `PaymentProviders:Stripe:MetadataSignatureSecret` | Vault `payments/stripe` | Secret for signing checkout session metadata |
| `PaymentProviders:PayPal:ClientId` | Vault `payments/paypal` | PayPal REST API client ID |
| `PaymentProviders:PayPal:ClientSecret` | Vault `payments/paypal` | PayPal REST API client secret |
| `PaymentProviders:PayPal:BaseUrl` | Vault `payments/paypal` | PayPal API base URL (default: `https://api-m.sandbox.paypal.com`) |
| `PaymentProviders:PayPal:WebhookId` | Vault `payments/paypal` | PayPal webhook ID for signature verification |
| `Vault:Enabled` | appsettings | Enable Vault integration |
| `Vault:Address` | appsettings | Vault server URL |
| `RabbitMQ:Host` / credentials | appsettings | MassTransit transport |

### Optional

| Key | Description |
|---|---|
| `Payments:DemoMode` | `true` (default) — `PaymentSessionRequestedConsumer` simulates provider responses without real Stripe calls. Set `false` in production. |
| `PaymentProviders:Stripe:BaseUrl` | Override Stripe API base URL (hermetic testing) |

---

## Database

- **Schema:** `payments`
- **DbContext:** `PaymentDbContext`
- **Migration runner:** `MigrateWithRetryAsync` on startup (skipped in `Test` environment)

### Key tables

| Table | Description |
|---|---|
| `payments.Payments` | Payment aggregates; indexed on `OrderId`, `ProviderTransactionId`, `ProviderSessionId` |
| `payments.Subscriptions` | Recurring subscriptions; indexed on `UserId`, `ProviderSubscriptionId` |
| `payments.SubscriptionPlans` | Plan catalog with provider price ID mapping |
| `payments.WebhookEvents` | Processed webhook events; unique on `(Provider, ProviderEventId)` |
| `payments.RefundSagas` | `RefundSaga` state; indexed on `OrderId`, `PaymentId`, `ProviderRefundId` |
| `payments.SubscriptionSagas` | `SubscriptionSaga` state; indexed on `ProviderSubscriptionId`, `UserId` |
| `payments.OutboxMessages` | MassTransit transactional outbox |
| `payments.OutboxState` | Outbox delivery state |
| `payments.InboxState` | Inbox deduplication |

### Column details (Payments table)

| Column | Type | Notes |
|---|---|---|
| `Amount` | `numeric(18,2)` | |
| `Currency` | `varchar(3)` | |
| `Status` | `varchar` | Stored as string (`PaymentStatus` enum) |
| `Provider` | `varchar` | Stored as string (`PaymentProvider` enum) |
| `ProviderTransactionId` | `varchar(255)` | |
| `ProviderSessionId` | `varchar(255)` | |

### Migrations

| Migration | Date | Description |
|---|---|---|
| `20260503202541_InitialCreate` | 2026-05-03 | Payments table, outbox/inbox |
| `20260504035338_AddSubscriptionAndWebhookEvents` | 2026-05-04 | Subscriptions, SubscriptionPlans, WebhookEvents |
| `20260514015704_AddRefundSagas` | 2026-05-14 | RefundSagas table |
| `20260514025243_AddSubscriptionSagas` | 2026-05-14 | SubscriptionSagas table |

---

## Testing

### Test projects

| Project | Location | Coverage |
|---|---|---|
| `Payments.Unit` | `tests/Payments/Payments.Unit/` | Domain model (`PaymentTests`), `PaymentGateway`, Stripe signature validator, `StripeWebhookProcessor`, `WebhookIdempotencyGuard`, subscription command/query handlers, Stripe checkout resilience (Polly) |
| `Payments.Integration` | `tests/Payments/Payments.Integration/` | Webhook flows (`WebhookFlowsTests`), webhook idempotency (`WebhookIdempotencyTests`), `RefundSaga` integration (`RefundSagaIntegrationTests`), subscription endpoints (`SubscriptionEndpointTests`), `SubscriptionSaga` (`SubscriptionSagaTests`) via `PaymentsWebAppFactory` |
| `Payments.Architecture` | `tests/Payments/Payments.Architecture/` | Dependency boundary enforcement |
| `Payments.Contract` | `tests/Payments/Payments.Contract/` | Pact contract tests (`PaymentEventsConsumerTests`) |

### Running tests

```bash
# Unit tests
dotnet test tests/Payments/Payments.Unit/

# Integration tests (requires Docker)
dotnet test tests/Payments/Payments.Integration/

# Architecture tests
dotnet test tests/Payments/Payments.Architecture/
```

Integration tests use `SharedTestPostgres.CreateDatabaseAsync("payments")` from `BuildingBlocks.Testing.Containers`. The `Test` environment sets `Payments:DemoMode=true` and stubs `Stripe:WebhookSecret` per-test. Raw Testcontainer instantiation is prohibited by `scripts/check-architecture.sh`.
