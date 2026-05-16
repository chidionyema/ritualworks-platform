# Brief: Port Stripe payment provider from monolith to microservices platform

You are working on `/Users/chidionyema/Documents/code/haworks-platform/` (the new microservices platform). The previous monolith lives at `/Users/chidionyema/Documents/code/haworks/` and is read-only context.

## Background — read this first

The monolith → microservices migration extracted the **saga skeleton** (MassTransit state machine, integration events, transactional outbox, consumers in Orders/Catalog/CheckoutOrchestrator) but did NOT port the **provider implementations** (Stripe SDK calls, webhook signature validation, amount-mismatch handling, idempotency guards). The new `Payments.Application/Consumers/PaymentSessionRequestedConsumer.cs:46-52` is a stub that throws `NotImplementedException` for non-demo mode.

Your job is to port the working Stripe code from the monolith into the new `Payments` service, with one critical architectural correction: **the monolith's processors directly mutate the Order aggregate via `IOrderRepository`, which is a cross-context violation.** In the new platform, Payments must publish `PaymentCompletedEvent` / `PaymentSessionFailedEvent` and let `Orders.Application/Consumers/PaymentCompletedConsumer.cs` + `PaymentSessionFailedConsumer.cs` react. Those consumers already exist and are wired correctly — do not touch them, and do not add any `IOrderRepository` reference to Payments.

The monolith's `.claude/rules/event-integration-rationale.md` was a proposal to evolve the monolith into exactly the pattern the new platform now has — that doc is essentially your migration spec.

## Project facts

- .NET 9, `<TargetFramework>net9.0</TargetFramework>`, `Nullable` enabled, `TreatWarningsAsErrors=true`.
- Repo-level `Directory.Build.props` defines `<NoWarn>` for accepted analyzer rules (CA1848, CA1873 etc.). Do not add new suppressions; if a new rule fires, fix the call site.
- Stripe SDK: use the `Stripe.net` NuGet package; pin to the same version the monolith uses (`grep -h "Stripe.net" /Users/chidionyema/Documents/code/haworks/src/**/*.csproj` to find it).
- The new platform follows clean architecture: `Payments.Domain` → `Payments.Application` → `Payments.Infrastructure` → `Payments.Api`. Domain has no external deps. Application defines interfaces, Infrastructure implements.
- Each service owns its own DB on Neon (Postgres). Payments uses `ConnectionStrings:payments`.
- Inter-service communication is via MassTransit + RabbitMQ outbox. Cross-context DB calls are forbidden.
- Local dev: `dotnet run --project deploy/aspire`. Aspire runs Postgres, Redis, RabbitMQ, Vault, MinIO, ClamAV, Tempo locally.
- Production: deployed to Fly.io. Vault is **disabled in production** — Identity reads `Jwt:SigningKeyPem` from config when `Vault:Enabled=false`. Same pattern applies to anything else that was Vault-bound.
- Build: `dotnet build src/Payments/Payments.Api/Payments.Api.csproj -c Release`.
- Test: `dotnet test tests/Payments.Unit`, `dotnet test tests/Payments.Integration`.

## Repo conventions you must follow

Read these BEFORE writing code (they apply to the new platform too):

- `/Users/chidionyema/Documents/code/haworks/.claude/rules/dotnet-clean-arch.md` — clean architecture, Result pattern, internal sealed handlers, IDomainEventPublisher (not IPublishEndpoint), event publishing BEFORE SaveChangesAsync, file-scoped namespaces.
- `/Users/chidionyema/Documents/code/haworks/.claude/rules/code-quality.md` — naming, primary constructors, structured logging with LoggerMessage source generators, no magic strings, constants files.
- `/Users/chidionyema/Documents/code/haworks/.claude/rules/options-configuration.md` — `Options` suffix, `SectionName` constant, `[Required]`/`[Range]` data annotations, `.AddOptions<T>().Bind(...).ValidateDataAnnotations().ValidateOnStart()`.
- `/Users/chidionyema/Documents/code/haworks/.claude/rules/security.md` — secrets via Vault (or via Fly secrets in prod-no-Vault mode), never in appsettings.json, JTI revocation, audit logging.
- `/Users/chidionyema/Documents/code/haworks/.claude/rules/resilience.md` — retry/circuit breaker/bulkhead/fallback patterns. The new platform has `Haworks.BuildingBlocks.Resilience.IResiliencePolicyFactory` already wired. Use it for the Stripe HTTP client.
- `/Users/chidionyema/Documents/code/haworks/.claude/rules/testing.md` — Fluent Assertions, Testcontainers, no `Task.Delay`, MassTransit test harness, Pact contracts for cross-context events.

## Files to port (Stripe — phase 1)

### Source files (monolith → platform)

| Monolith path | Platform target | Notes |
|---|---|---|
| `src/Application/Interfaces/Payments/IPaymentGateway.cs` | `src/Payments/Payments.Application/Interfaces/IPaymentGateway.cs` | Drop the `IOrderRepository`-bound members. Keep `Checkout`, `Webhooks`, `CheckHealthAsync`. Subscriptions and Refunds are out of scope for phase 1 — leave those interface members but make them optional (return `Task.FromException<>(new NotSupportedException(...))` in the impl, or split into separate interfaces deferred to phase 2). |
| `src/Application/Interfaces/Payments/ICheckoutSessionService.cs` (locate the exact path) | `src/Payments/Payments.Application/Interfaces/ICheckoutSessionService.cs` | |
| `src/Application/Interfaces/Payments/IWebhookProcessor.cs` (locate) | `src/Payments/Payments.Application/Interfaces/IWebhookProcessor.cs` | |
| `src/Infrastructure/Options/PaymentProviderOptions.cs` | `src/Payments/Payments.Infrastructure/Options/PaymentProviderOptions.cs` | |
| `src/Infrastructure/Payments/Stripe/StripeConstants.cs` | `src/Payments/Payments.Infrastructure/Stripe/StripeConstants.cs` | |
| `src/Infrastructure/Payments/Stripe/StripeValidationHelper.cs` | `src/Payments/Payments.Infrastructure/Stripe/StripeValidationHelper.cs` | |
| `src/Infrastructure/Payments/Stripe/StripeClientFactory.cs` | `src/Payments/Payments.Infrastructure/Stripe/StripeClientFactory.cs` | |
| `src/Infrastructure/Payments/Stripe/StripeCheckoutSessionService.cs` | `src/Payments/Payments.Infrastructure/Stripe/StripeCheckoutSessionService.cs` | |
| `src/Infrastructure/Payments/Stripe/StripePaymentProcessor.cs` | `src/Payments/Payments.Infrastructure/Stripe/StripePaymentProcessor.cs` | **MUST STRIP** all `IOrderRepository` usage (~lines 22, 195-200, 228 of monolith file). Replace with `IDomainEventPublisher.PublishAsync(new PaymentCompletedEvent { ... })` BEFORE `SaveChangesAsync`. |
| `src/Infrastructure/Payments/Stripe/StripeWebhookProcessor.cs` | `src/Payments/Payments.Infrastructure/Stripe/StripeWebhookProcessor.cs` | Same correction — strip Order mutation, publish event. Keep `Stripe.EventUtility.ConstructEvent` signature validation. |
| `src/Infrastructure/Payments/Stripe/StripePaymentSessionCacheService.cs` | `src/Payments/Payments.Infrastructure/Stripe/StripePaymentSessionCacheService.cs` | Uses Redis. New platform has `ConnectionStrings:redis` from Upstash on prod, Aspire Redis container on local. |
| `src/Infrastructure/Payments/PaymentGateway.cs` | `src/Payments/Payments.Infrastructure/PaymentGateway.cs` | Drop the cross-provider mismatch check — outbox dedup handles it. |
| `src/Infrastructure/Payments/WebhookIdempotencyGuard.cs` | `src/Payments/Payments.Infrastructure/Webhooks/WebhookIdempotencyGuard.cs` | Backed by Redis (DistributedCache). |
| `src/Infrastructure/Payments/PaymentAmountMismatchHandler.cs` | `src/Payments/Payments.Application/Webhooks/PaymentAmountMismatchHandler.cs` | Convert Order mutation to publishing `PaymentAmountMismatchEvent` (already exists in `Contracts`). |
| `src/Infrastructure/Payments/IdempotencyKeyGenerator.cs` | `src/Payments/Payments.Application/Common/IdempotencyKeyGenerator.cs` | Direct copy. |
| `src/Infrastructure/Payments/PaymentValidationHelper.cs` | `src/Payments/Payments.Application/Common/PaymentValidationHelper.cs` | Direct copy. |
| `src/Infrastructure/Payments/WebhookHeaders.cs` | `src/Payments/Payments.Infrastructure/Webhooks/WebhookHeaders.cs` | Direct copy. |
| `src/Infrastructure/Payments/PaymentServiceExtensions.cs` (DI registration) | merge into `src/Payments/Payments.Infrastructure/DependencyInjection.cs` | Add the Stripe registrations alongside the existing MassTransit / EF wiring. |

### Phase 2 — required follow-up (not optional)

Phase 1 alone does not give the user a working system. After phase 1 PRs land,
phase 2 must follow before the payments work is considered complete. A separate
brief should be authored at `docs/agent-briefs/payments-port-phase-2.md` covering:

- **PayPal full provider port** — all of `src/Infrastructure/Payments/PayPal/**`
  (`PayPalCheckoutService`, `PayPalPaymentProcessor`, `PayPalWebhookProcessor`,
  `PayPalSubscriptionManager`, `PayPalRefundService`, `PayPalClientFactory`,
  `PayPalEndpoints`, `PayPalModels`, `PayPalJsonOptions`). Same architectural
  correction as Stripe — strip every `IOrderRepository` reference, replace with
  `IDomainEventPublisher.PublishAsync(...)` BEFORE `SaveChangesAsync`.
- **Stripe subscriptions** — `StripeSubscriptionManager.cs`,
  `StripeSubscriptionService.cs`, `StripeSubscriptionStatusMapper.cs`. Powers
  the recurring-billing path. Likely needs new domain events
  (`SubscriptionStartedEvent`, `SubscriptionRenewedEvent`,
  `SubscriptionCancelledEvent`) added to `src/Contracts/Payments/`.
- **Stripe refunds** — `StripeRefundService.cs`. Likely needs `RefundIssuedEvent`.
- **`PaymentProviderHealthCheck.cs`** — wire into Payments.Api's
  `/health/ready` endpoint so Fly + AppHost can detect provider outages.
- **`WebhookRouter.cs`** — reintroduce once PayPal is back; the controller
  dispatches by `Provider` header to either Stripe or PayPal processor.

Phase 2 acceptance: a real PayPal checkout completes end-to-end via the same
saga, a Stripe subscription renews via webhook, a refund issued via API
publishes the right event, and `/health/ready` flips to unhealthy when the
provider's API key is invalidated.

The phase-1 PRs are scoped narrowly so phase 2 can ship as a focused effort
right behind them — do not treat phase 1 closure as the end of the migration.

### Wire into the new platform's existing consumers

These files exist and need to be edited (not replaced):

- `src/Payments/Payments.Application/Consumers/PaymentSessionRequestedConsumer.cs:46-52` — replace the `NotImplementedException` block with a call to `ICheckoutSessionService.CreateSessionAsync(...)`. On success, publish `PaymentSessionCreatedEvent` (already done correctly in lines around 80-100). On failure, publish `PaymentSessionFailedEvent`.
- `src/Payments/Payments.Application/Consumers/PaymentWebhookValidatedConsumer.cs` — the parsing skeleton at lines 128-259 is incomplete. Delegate to the ported `StripeWebhookProcessor.ProcessEventAsync(...)` instead of re-implementing parsing inline. Keep the deterministic `MessageId` (SHA256 hash) for inbox dedup that's already there.
- `src/Payments/Payments.Api/Controllers/WebhooksController.cs` — confirm the `POST /webhooks/stripe` endpoint reads the raw body (for signature validation), validates via the ported `StripeWebhookProcessor.ValidateSignature(...)`, and publishes `PaymentWebhookValidatedEvent` with the raw payload + headers.

### Hardcoded Stripe redirect URLs — fix these

`src/CheckoutOrchestrator/CheckoutOrchestrator.Application/Sagas/CheckoutSaga.cs:119-120` hardcodes `https://app.example.com/checkout/{success,cancel}`. Move to a new `CheckoutOptions` class:

```csharp
public sealed class CheckoutOptions
{
    public const string SectionName = "Checkout";
    [Required, Url] public string SuccessUrl { get; set; } = "";
    [Required, Url] public string CancelUrl { get; set; } = "";
}
```

Bind in `CheckoutOrchestrator.Infrastructure/DependencyInjection.cs`. The saga reads them via `IOptions<CheckoutOptions>` and passes them through `PaymentSessionRequestedEvent` to the Payments service.

## Tests to port

| Monolith path | Platform target |
|---|---|
| `tests/haworks.Tests.Unit/Payments/**/*.cs` | `tests/Payments.Unit/` (new subfolder per test class) |
| `tests/haworks.Tests.integration/Saga/CheckoutSagaEndToEndTests.cs` | `tests/CheckoutOrchestrator.Integration/CheckoutSagaEndToEndTests.cs` |
| `tests/haworks.Tests.integration/Saga/CheckoutSagaFlowBEndToEndTests.cs` | `tests/CheckoutOrchestrator.Integration/CheckoutSagaFlowBEndToEndTests.cs` |
| `tests/haworks.Tests.integration/Chaos/CheckoutChaosTests.cs` | `tests/CheckoutOrchestrator.Integration/Chaos/CheckoutChaosTests.cs` |
| `tests/haworks.Tests.integration/_Builders/CheckoutSessionRequestBuilder.cs` | `tests/Payments.Unit/_Builders/` (or shared via `tests/BuildingBlocks.Testing`) |

Test corrections during port:
1. Drop any references to `IOrderRepository` mocks in Payment-side tests — those tests should now assert on **published events** via MassTransit's `ITestHarness`.
2. The integration tests use the monolith's `WebApplicationFactory<Program>` against the monolith's `Program.cs`. In the new platform you target the per-service Program (e.g. `WebApplicationFactory<Haworks.Payments.Api.Program>`).
3. CheckoutSaga E2E tests must bring up CheckoutOrchestrator + Payments + Catalog + Orders services (or use MassTransit InMemory test harness with all consumers registered, which is the existing convention in `tests/CheckoutOrchestrator.Integration`).
4. Chaos tests use Testcontainers' `PauseAsync()` for RabbitMQ/Postgres. Confirm the new platform's `BuildingBlocks.Testing` already exposes the chaos helpers; if not, port them too.

## Acceptance criteria

You're done when:

1. `dotnet build HaworksPlatform.sln -c Release` succeeds with 0 warnings, 0 errors.
2. `dotnet test tests/Payments.Unit/Payments.Unit.csproj` passes.
3. `dotnet test tests/Payments.Integration/Payments.Integration.csproj` passes (Testcontainers required — Docker must be running).
4. `dotnet test tests/CheckoutOrchestrator.Integration/CheckoutOrchestrator.Integration.csproj` passes, including the ported saga E2E and chaos tests.
5. `dotnet run --project deploy/aspire` starts cleanly. Hitting `POST /api/checkouts/initiate` on the BFF (`http://localhost:5050`) drives a saga to `Completed` state. Verify by querying `GET /api/checkouts/{sagaId}` — saga state should be `Completed`, Order should be `Paid`, Payment should be `Completed`.
6. `grep -r "IOrderRepository" src/Payments/` returns nothing — Payments must not reference the Orders aggregate.
7. `grep -r "NotImplementedException" src/Payments/Payments.Application/Consumers/` returns nothing in the production code paths.
8. New `CheckoutOptions` is read from configuration (verify via the saga creating a Stripe session whose `success_url` matches what's set in `appsettings.json`).
9. The Stripe.net package is added only to `Payments.Infrastructure.csproj`. Application/Domain remain SDK-free.

## Suggested commit / PR shape

Don't ship one giant PR. Split as:

1. **PR 1 — Foundation**: `IPaymentGateway`/`ICheckoutSessionService`/`IWebhookProcessor` interfaces, `PaymentProviderOptions`, `StripeConstants`, `StripeValidationHelper`, `StripeClientFactory`, `IdempotencyKeyGenerator`, `PaymentValidationHelper`, `WebhookHeaders`, `CheckoutOptions`. No behavioral change yet — just shape.
2. **PR 2 — Stripe checkout**: `StripeCheckoutSessionService`, `StripePaymentSessionCacheService`, `StripePaymentProcessor` (with Order-mutation strip-out), wired into `PaymentSessionRequestedConsumer`. Phase 1 unit tests included.
3. **PR 3 — Stripe webhooks**: `StripeWebhookProcessor`, `WebhookIdempotencyGuard`, `PaymentAmountMismatchHandler`, wired into `PaymentWebhookValidatedConsumer` + `WebhooksController`. Phase 2 unit tests + webhook signature validation tests included.
4. **PR 4 — Saga E2E + chaos tests**: port the monolith's saga integration tests + chaos tests.
5. **PR 5 (optional)** — config-driven Stripe redirect URLs (CheckoutOptions wiring through the saga's `PaymentSessionRequested` event).

## Pitfalls

1. **`TreatWarningsAsErrors=true` plus `<AnalysisLevel>latest-recommended</AnalysisLevel>`.** Newer SDK patches surface new analyzer rules — CA1873 already bit us once. If a new rule fires, fix the call site (e.g. wrap in `if (_logger.IsEnabled(LogLevel.Information))`) rather than adding to `<NoWarn>`.
2. **Outbox atomicity.** Publish events BEFORE `SaveChangesAsync`. The MassTransit-EF outbox stores events in the same DB transaction as the Payment aggregate update.
3. **Stripe webhook signature validation.** Production failure-mode: if the raw body is buffered after model binding, `Stripe.EventUtility.ConstructEvent` rejects the signature. The new platform's `WebhooksController` must read the raw body via `Request.Body` BEFORE any model binding (the monolith handles this in its `WebhooksController.PostAsync` — replicate exactly).
4. **No `Task.Delay` in tests** (monolith convention, applies here too). Use polling loops with timeouts via `BuildingBlocks.Testing` helpers.
5. **Stripe API key on Fly.** No Vault in prod — the bootstrap script should add a `STRIPE_SECRET_KEY` field to `deploy/fly/.env.example` and set it as `Stripe__SecretKey` on `haworks-payments`. The webhook secret is already templated as `STRIPE_WEBHOOK_SECRET`.
6. **Idempotency key must include UserId** (per `code-quality.md`): `hash($userId:$clientKey)`. Don't drop this from the ported `IdempotencyKeyGenerator`.
7. **Stripe SDK version pinning.** Match the monolith's pinned version. A major SDK bump risks breaking changes in `EventUtility` or `SessionService`.

## What you DO NOT need to do

- Don't touch `Orders`, `Catalog`, or `BFF` — they consume Payments events correctly already.
- Don't re-implement the saga state machine. `CheckoutOrchestrator.Application/Sagas/CheckoutSaga.cs` is correct.
- Don't add `IOrderRepository` to Payments. Ever.
- Don't port subscriptions, refunds, or PayPal — phase 2.
- Don't change the `.github/workflows/ci.yml` or the Fly deploy scripts unless your test additions require new test projects to be added to the integration matrix in `ci.yml`.
- Don't write a README or migration doc unless explicitly asked.

## How to verify locally

```bash
# Build everything
dotnet build HaworksPlatform.sln -c Release

# Run unit tests for Payments + CheckoutOrchestrator
dotnet test tests/Payments.Unit
dotnet test tests/CheckoutOrchestrator.Unit

# Integration tests (Docker required for Testcontainers)
dotnet test tests/Payments.Integration
dotnet test tests/CheckoutOrchestrator.Integration

# E2E sanity — drive a real saga locally
dotnet run --project deploy/aspire &
# wait for "Aspire running" log
curl -X POST http://localhost:5050/api/checkouts/initiate \
  -H "Content-Type: application/json" \
  -H "X-Idempotency-Key: $(uuidgen)" \
  -d '{"orderId":"...","items":[...],"totalAmount":99.99,"currency":"USD"}'
# Then poll GET /api/checkouts/{sagaId} until State=="Completed"
```

When you have a green build + green tests, hand it back with the commit hashes for each PR.
