# Brief: Payments port phase 2 — PayPal + subscriptions + refunds + health

You are continuing the payments port from `/Users/chidionyema/Documents/code/haworks/` (monolith, read-only) into `/Users/chidionyema/Documents/code/haworks-platform/` (the new microservices platform). **Phase 1 must be merged before you start phase 2.** Phase 1 is described in `docs/agent-briefs/payments-port-from-monolith.md` and ports Stripe checkout + webhooks + idempotency guard + amount-mismatch handler.

This brief covers phase 2: everything the monolith had that phase 1 deferred. After phase 2 ships, the new Payments service should match the monolith's payments capability surface.

## Why this exists

Phase 1 made the new platform able to take Stripe credit-card payments through the saga. It does not yet support: PayPal, recurring subscriptions, refunds, provider health probes, or webhook routing across multiple providers. The user's goal is a working system equivalent to the monolith — phase 1 alone is not that.

This is committed work, not a backlog item. The product owner has explicitly said: phase 1 closure is not "done."

## Project facts (carry over from phase 1)

- .NET 9, `Nullable` enabled, `TreatWarningsAsErrors=true`. Repo `Directory.Build.props` has the accepted-rule suppressions; don't add new ones.
- `Stripe.net` already pinned in `Payments.Infrastructure.csproj` from phase 1.
- PayPal SDK: monolith uses **a hand-rolled HTTP client** (`PayPalClientFactory`, `PayPalEndpoints`, `PayPalModels`) — there is no PayPal SDK NuGet. Port that as-is; don't introduce a third-party PayPal package.
- Clean architecture: `Payments.Domain → Application → Infrastructure → Api`. SDKs and HTTP clients live in Infrastructure only.
- Inter-service: MassTransit + RabbitMQ outbox. **No cross-context DB calls.** Same architectural correction as phase 1 — every monolith file that injected `IOrderRepository` mutates Order directly, and every one of those mutations becomes a `IDomainEventPublisher.PublishAsync(...)` BEFORE `SaveChangesAsync`.
- Local dev: `dotnet run --project deploy/aspire`.
- Production: Fly.io. No Vault. Provider keys via Fly secrets.
- CI: `.github/workflows/ci.yml` runs unit + integration matrix per service. Adding new test classes to existing projects is fine; adding new test PROJECTS requires editing the workflow.

## Repo conventions (re-read before writing)

Same set as phase 1:
- `/Users/chidionyema/Documents/code/haworks/.claude/rules/dotnet-clean-arch.md`
- `/Users/chidionyema/Documents/code/haworks/.claude/rules/code-quality.md`
- `/Users/chidionyema/Documents/code/haworks/.claude/rules/options-configuration.md`
- `/Users/chidionyema/Documents/code/haworks/.claude/rules/security.md`
- `/Users/chidionyema/Documents/code/haworks/.claude/rules/resilience.md`
- `/Users/chidionyema/Documents/code/haworks/.claude/rules/testing.md`

The non-negotiables: `internal sealed` handlers/consumers, `Options` suffix + `SectionName` constant + `ValidateOnStart()`, publish events BEFORE `SaveChangesAsync`, `IDomainEventPublisher` not `IPublishEndpoint`, no `Task.Delay` in tests, Fluent Assertions.

## Files to port — phase 2

### Group A: Application-layer interfaces

These are probably already partially in place from phase 1's `IPaymentGateway` port. Phase 2 fills in the rest.

| Monolith path | Platform target | Notes |
|---|---|---|
| `src/Application/Interfaces/Payments/IRefundService.cs` | `src/Payments/Payments.Application/Interfaces/IRefundService.cs` | Includes `RefundRequest`, `RefundResult`, `RefundStatus` enum. Direct copy. |
| `src/Application/Interfaces/Payments/ISubscriptionManager.cs` | `src/Payments/Payments.Application/Interfaces/ISubscriptionManager.cs` | Includes `SubscriptionStatusResult`, `SubscriptionEvent`, `SubscriptionEventType`, `SubscriptionStatus`. Direct copy. |
| `src/Application/Interfaces/Payments/IWebhookRouter.cs` | `src/Payments/Payments.Application/Interfaces/IWebhookRouter.cs` | Direct copy. |

If `IPaymentGateway.cs` was simplified in phase 1 to drop subscriptions/refunds, restore those properties now (the `PaymentGateway` facade re-aggregates them).

### Group B: Stripe subscriptions + refunds

| Monolith path | Platform target | Notes |
|---|---|---|
| `src/Infrastructure/Payments/Stripe/StripeSubscriptionStatusMapper.cs` | `src/Payments/Payments.Infrastructure/Stripe/StripeSubscriptionStatusMapper.cs` | 24 LOC, direct copy. |
| `src/Infrastructure/Payments/Stripe/StripeSubscriptionService.cs` | `src/Payments/Payments.Infrastructure/Stripe/StripeSubscriptionService.cs` | Creates subscription checkout sessions. Stateless — direct copy after namespace adjust. |
| `src/Infrastructure/Payments/Stripe/StripeSubscriptionManager.cs` | `src/Payments/Payments.Infrastructure/Stripe/StripeSubscriptionManager.cs` | **CHECK for `IOrderRepository` injection** — if present, strip and replace with `IDomainEventPublisher` + the new `Subscription*` events listed below. |
| `src/Infrastructure/Payments/Stripe/StripeRefundService.cs` | `src/Payments/Payments.Infrastructure/Stripe/StripeRefundService.cs` | 217 LOC. **CHECK for `IOrderRepository`** — strip if present. On successful refund, publish `RefundIssuedEvent` BEFORE saving. |

### Group C: PayPal full provider (the bulk of phase 2)

| Monolith path | Platform target | Notes |
|---|---|---|
| `src/Infrastructure/Payments/PayPal/PayPalEndpoints.cs` | `src/Payments/Payments.Infrastructure/PayPal/PayPalEndpoints.cs` | Sandbox + live URL constants. Direct copy. |
| `src/Infrastructure/Payments/PayPal/PayPalJsonOptions.cs` | `src/Payments/Payments.Infrastructure/PayPal/PayPalJsonOptions.cs` | Direct copy. |
| `src/Infrastructure/Payments/PayPal/PayPalModels.cs` | `src/Payments/Payments.Infrastructure/PayPal/PayPalModels.cs` | 353 LOC of DTOs for PayPal API. Direct copy. |
| `src/Infrastructure/Payments/PayPal/PayPalClientFactory.cs` | `src/Payments/Payments.Infrastructure/PayPal/PayPalClientFactory.cs` | OAuth2 client-credentials flow. Wire its `HttpClient` through `IResiliencePolicyFactory` (use `ResilienceOptions.PayPal` from BuildingBlocks). |
| `src/Infrastructure/Payments/PayPal/PayPalCheckoutService.cs` | `src/Payments/Payments.Infrastructure/PayPal/PayPalCheckoutService.cs` | Stateless. Implements `ICheckoutSessionService` for PayPal. |
| `src/Infrastructure/Payments/PayPal/PayPalRefundService.cs` | `src/Payments/Payments.Infrastructure/PayPal/PayPalRefundService.cs` | **STRIP** any `IOrderRepository` use. Publish `RefundIssuedEvent`. |
| `src/Infrastructure/Payments/PayPal/PayPalSubscriptionManager.cs` | `src/Payments/Payments.Infrastructure/PayPal/PayPalSubscriptionManager.cs` | **STRIP** `IOrderRepository`. Publish subscription events instead. |
| `src/Infrastructure/Payments/PayPal/PayPalPaymentProcessor.cs` | `src/Payments/Payments.Infrastructure/PayPal/PayPalPaymentProcessor.cs` | **STRIP** `IOrderRepository` (lines 20, 203-208 in monolith — same pattern as Stripe). Publish `PaymentCompletedEvent`. |
| `src/Infrastructure/Payments/PayPal/PayPalWebhookProcessor.cs` | `src/Payments/Payments.Infrastructure/PayPal/PayPalWebhookProcessor.cs` | **STRIP** `IOrderRepository`. Implements `IWebhookProcessor` for PayPal. Validates signature via PayPal's webhook verification API (different shape than Stripe — keep the monolith's logic verbatim). |

### Group D: Re-introduce webhook routing + provider health

| Monolith path | Platform target | Notes |
|---|---|---|
| `src/Infrastructure/Payments/WebhookRouter.cs` | `src/Payments/Payments.Infrastructure/Webhooks/WebhookRouter.cs` | Phase 1 dropped this (single provider). Restore now. Routes incoming webhooks by header (`Stripe-Signature` → Stripe processor, `PAYPAL-TRANSMISSION-SIG` → PayPal processor). |
| `src/Infrastructure/Payments/PaymentProviderHealthCheck.cs` | `src/Payments/Payments.Infrastructure/Health/PaymentProviderHealthCheck.cs` | Implements `IHealthCheck`. Wire into `Payments.Api/Program.cs`'s `services.AddHealthChecks().AddCheck<PaymentProviderHealthCheck>("payment-providers", tags: ["ready"])`. The existing `/health/ready` endpoint (from `MapDefaultEndpoints` / ServiceDefaults) will pick it up. |

### Group E: New API endpoints

The monolith exposes a `SubscriptionController.cs` at `src/Api/Controllers/`. In the new platform, subscription endpoints belong in `Payments.Api/Controllers/SubscriptionsController.cs`. Routes to expose:

- `POST /api/subscriptions` — create subscription checkout (calls `StripeSubscriptionService.CreateCheckoutSessionAsync` or PayPal equivalent based on `PaymentProviderOptions.Active`).
- `GET /api/subscriptions/{subscriptionId}` — return current status via `ISubscriptionManager.GetStatusAsync`.
- `POST /api/subscriptions/{subscriptionId}/cancel` — cancel via `ISubscriptionManager.CancelAsync`.

Authorization: `[Authorize]` (the JWT bearer scheme is already wired via Identity/BFF). Rate-limit subscriptions creation with the existing `auth` policy or add a new `subscriptions` policy with sensible limits.

The BFF's `BackendClients.cs` does NOT currently include a subscription HttpClient; if you want the BFF to proxy these endpoints (recommended — keeps the public surface single-origin), add one with the same Aspire service-discovery URI pattern used for `payments-svc`. If not needed for v1, skip and let consumers hit `haworks-payments.flycast` directly (only practical if there's no public client; document the choice in the PR).

## New domain events to add

These didn't exist in `src/Contracts/Payments/` from phase 1 because phase 1 didn't need them. Add them now as immutable records following the pattern in existing event files (`PaymentCompletedEvent.cs` etc.):

```csharp
// src/Contracts/Payments/RefundIssuedEvent.cs
public sealed record RefundIssuedEvent
{
    public required Guid PaymentId { get; init; }
    public required Guid OrderId { get; init; }
    public required string RefundId { get; init; }       // Provider's refund ID
    public required long AmountCents { get; init; }
    public required string Currency { get; init; }
    public required PaymentProvider Provider { get; init; }
    public string? Reason { get; init; }
    public DateTime IssuedAt { get; init; } = DateTime.UtcNow;
}

// src/Contracts/Payments/SubscriptionStartedEvent.cs
public sealed record SubscriptionStartedEvent
{
    public required string SubscriptionId { get; init; }  // Provider's subscription ID
    public required string UserId { get; init; }
    public required string PlanId { get; init; }
    public required PaymentProvider Provider { get; init; }
    public DateTime CurrentPeriodEnd { get; init; }
    public DateTime StartedAt { get; init; } = DateTime.UtcNow;
}

// src/Contracts/Payments/SubscriptionRenewedEvent.cs
public sealed record SubscriptionRenewedEvent
{
    public required string SubscriptionId { get; init; }
    public required string UserId { get; init; }
    public required PaymentProvider Provider { get; init; }
    public required long AmountCents { get; init; }
    public required string Currency { get; init; }
    public DateTime NewPeriodEnd { get; init; }
    public DateTime RenewedAt { get; init; } = DateTime.UtcNow;
}

// src/Contracts/Payments/SubscriptionCancelledEvent.cs
public sealed record SubscriptionCancelledEvent
{
    public required string SubscriptionId { get; init; }
    public required string UserId { get; init; }
    public required PaymentProvider Provider { get; init; }
    public string? Reason { get; init; }
    public DateTime CancelledAt { get; init; } = DateTime.UtcNow;
}
```

If `PaymentProvider` enum lives only in monolith Domain, port the enum to `src/Contracts/Payments/PaymentProvider.cs` (it's referenced by every event).

### Pact contracts

Per `testing.md` rule: **all cross-context events MUST have Pact contracts.** Add Pact tests for the four new events under `tests/Payments.Contract/`:

- Producer side: Payments publishes the event with the contracted shape.
- Consumer side: any subscriber service (Identity? for tying subscription status to user?) verifies it matches the contract.

If no current service consumes subscription events directly (likely — they may be portfolio-only for now), the producer-side contract still goes in to lock the event shape.

## Wire-up points in existing files

These exist; don't replace, edit:

- `src/Payments/Payments.Infrastructure/DependencyInjection.cs` — register the new providers conditionally based on `PaymentProviderOptions.Active`. Stripe stays the default. Both providers must be available so `WebhookRouter` can dispatch to either.
- `src/Payments/Payments.Application/Consumers/PaymentWebhookValidatedConsumer.cs` — phase 1 wired this to `StripeWebhookProcessor` directly. Switch it to delegate via `IWebhookRouter.RouteAsync(...)` so PayPal events also flow.
- `src/Payments/Payments.Api/Controllers/WebhooksController.cs` — add a `POST /webhooks/paypal` route mirroring `/webhooks/stripe`. Both routes feed the same `PaymentWebhookValidatedEvent`; the router dispatches downstream.
- `src/Payments/Payments.Api/Program.cs` — register `PaymentProviderHealthCheck` against `/health/ready`. Add the new `SubscriptionsController` route group under `/api/subscriptions` with the rate-limit policy.

## Config additions

Per `options-configuration.md`. Update `PaymentProviderOptions` to validate both providers' configs when `Active` covers both (the validator pattern).

Add to `deploy/fly/.env.example`:

```bash
# PayPal — required if Active provider includes PayPal
PAYPAL_CLIENT_ID=
PAYPAL_CLIENT_SECRET=
PAYPAL_WEBHOOK_ID=
PAYPAL_ENVIRONMENT=Sandbox        # Sandbox | Live

# Stripe subscription / refund (already have webhook secret + secret key from phase 1)
STRIPE_PRICE_ID_DEFAULT=           # default subscription price ID for the demo
```

Update `deploy/fly/bootstrap.sh` to set these on `haworks-payments`:
- `PaymentProviders__PayPal__ClientId=$PAYPAL_CLIENT_ID`
- `PaymentProviders__PayPal__ClientSecret=$PAYPAL_CLIENT_SECRET`
- `PaymentProviders__PayPal__WebhookId=$PAYPAL_WEBHOOK_ID`
- `PaymentProviders__PayPal__Environment=$PAYPAL_ENVIRONMENT`
- `PaymentProviders__Stripe__DefaultPriceId=$STRIPE_PRICE_ID_DEFAULT`

Don't store these in committed files. Per the existing credential-handling rule.

## Tests to port

Direct mappings from monolith → new platform:

| Monolith path | Platform target |
|---|---|
| `tests/haworks.Tests.Unit/Payments/PayPalPaymentProcessorTests.cs` | `tests/Payments.Unit/PayPal/PayPalPaymentProcessorTests.cs` |
| `tests/haworks.Tests.Unit/Payments/PayPalWebhookProcessorTests.cs` | `tests/Payments.Unit/PayPal/PayPalWebhookProcessorTests.cs` |
| `tests/haworks.Tests.Unit/Payments/PaymentProviderHealthCheckTests.cs` | `tests/Payments.Unit/Health/PaymentProviderHealthCheckTests.cs` |
| `tests/haworks.Tests.Unit/Payments/WebhookRouterTests.cs` | `tests/Payments.Unit/Webhooks/WebhookRouterTests.cs` |
| `tests/haworks.Tests.Unit/Validators/SubscriptionValidatorTests.cs` | `tests/Payments.Unit/Validators/SubscriptionValidatorTests.cs` |
| `tests/haworks.Tests.Unit/Controllers/SubscriptionControllerTests.cs` | `tests/Payments.Unit/Controllers/SubscriptionsControllerTests.cs` |
| `tests/haworks.Tests.Unit/Commands/Subscriptions/CreateSubscriptionCheckoutCommandHandlerTests.cs` | `tests/Payments.Unit/Subscriptions/CreateSubscriptionCheckoutHandlerTests.cs` |
| `tests/haworks.Tests.integration/_Builders/SubscriptionRequestBuilder.cs` | `tests/Payments.Unit/_Builders/SubscriptionRequestBuilder.cs` (or `tests/BuildingBlocks.Testing/Builders/`) |

Test corrections during port:
1. Strip `IOrderRepository` mocks from PayPal tests (same as phase 1). Assert on **published events** via MassTransit `ITestHarness`.
2. PayPal tests use `HttpMessageHandler` mocks against `PayPalEndpoints`. The new platform's `BuildingBlocks.Testing` already exposes a `MockHttpMessageHandler`; reuse it instead of bringing the monolith's bespoke version.
3. Replace `WebApplicationFactory<Program>` (monolith) with `WebApplicationFactory<Haworks.Payments.Api.Program>`.
4. Add at least one **integration test** that drives a full PayPal webhook through `PaymentWebhookValidatedConsumer → IWebhookRouter → PayPalWebhookProcessor → IDomainEventPublisher` with the MassTransit harness asserting `PaymentCompletedEvent` is published. Place it in `tests/Payments.Integration/PayPalWebhookFlowTests.cs`.
5. Add at least one **chaos test** that pauses PayPal's mock endpoint mid-checkout to confirm the resilience policy retries and the saga compensates correctly. Place it in `tests/CheckoutOrchestrator.Integration/Chaos/PayPalChaosTests.cs`.

## Acceptance criteria

You're done when:

1. `dotnet build HaworksPlatform.sln -c Release` → 0 warnings, 0 errors.
2. `dotnet test tests/Payments.Unit` → green, with all phase 2 test classes passing.
3. `dotnet test tests/Payments.Integration` → green (Docker required).
4. `dotnet test tests/CheckoutOrchestrator.Integration` → green, including the new `PayPalChaosTests`.
5. `dotnet test tests/Payments.Contract` → green; new `RefundIssuedEvent` / `SubscriptionStartedEvent` / etc. have producer-side Pact files.
6. `grep -r "IOrderRepository" src/Payments/` → empty.
7. `dotnet run --project deploy/aspire` boots cleanly. With `PaymentProviders:Active=PayPal`:
   - `POST /api/checkouts/initiate` drives the saga to `Completed` via PayPal's sandbox.
   - `POST /api/subscriptions` creates a Stripe subscription session and a webhook landing publishes `SubscriptionStartedEvent`.
   - `POST /api/refunds` (or wherever the new platform exposes refunds) on a paid order publishes `RefundIssuedEvent` and updates the Payment aggregate.
8. `GET /health/ready` on `haworks-payments` returns 200 when both provider keys are valid; returns 503 when either is invalidated.
9. Webhook router correctly dispatches: a Stripe-signed payload to `StripeWebhookProcessor`, a PayPal-signed payload to `PayPalWebhookProcessor`, and rejects unsigned payloads.

## Suggested PR shape — split into 5

Same scoping discipline as phase 1.

1. **PR 6 — Contracts + interfaces**: new `RefundIssuedEvent`, `SubscriptionStartedEvent`, `SubscriptionRenewedEvent`, `SubscriptionCancelledEvent`, `PaymentProvider` enum (if not already in Contracts), `IRefundService`, `ISubscriptionManager`, `IWebhookRouter`. Pact producer-side stubs. No behavioural change.
2. **PR 7 — Stripe subscriptions + refunds**: `StripeSubscriptionStatusMapper`, `StripeSubscriptionService`, `StripeSubscriptionManager`, `StripeRefundService` ported with Order strip-out. Unit tests included.
3. **PR 8 — PayPal foundation**: `PayPalEndpoints`, `PayPalJsonOptions`, `PayPalModels`, `PayPalClientFactory`, `PayPalCheckoutService`. No payment processing yet; just the wire layer + checkout session creation. Smoke test that `PayPalClientFactory` can OAuth against the sandbox.
4. **PR 9 — PayPal payments + webhooks + router**: `PayPalPaymentProcessor`, `PayPalWebhookProcessor`, `PayPalRefundService`, `PayPalSubscriptionManager`, `WebhookRouter` reintroduced, `WebhooksController` gets `/webhooks/paypal` route. `PaymentWebhookValidatedConsumer` switched to use the router. Unit + integration tests included.
5. **PR 10 — Health + subscriptions API + chaos tests**: `PaymentProviderHealthCheck` wired into `/health/ready`. `SubscriptionsController` exposed. `PayPalChaosTests` added. Bootstrap script updated with the new env vars. End-to-end Aspire run verified.

## Pitfalls

1. **PayPal webhook signature validation is asynchronous.** Stripe validates locally with HMAC; PayPal hits PayPal's API to verify (`/v1/notifications/verify-webhook-signature`). The processor needs to call that endpoint and treat its response as the validation result. Don't try to do it locally — there's no public verification key.
2. **PayPal OAuth token caching.** `PayPalClientFactory` caches the access token in memory and refreshes near expiry. On Fly, multiple machines mean multiple in-memory caches; that's fine because each machine OAuths independently. Don't try to share the token via Redis — adds coupling for marginal gain.
3. **Subscription webhooks have N stages.** Stripe alone fires `customer.subscription.created`, `invoice.paid` (initial), `invoice.paid` (renewal), `customer.subscription.updated`, `customer.subscription.deleted`. The processor must distinguish between the initial paid invoice (publishes `SubscriptionStartedEvent`) and renewals (publishes `SubscriptionRenewedEvent`) by checking `billing_reason`. The monolith's `StripeWebhookProcessor` has the dispatch logic — preserve it exactly.
4. **Refunds are async with both providers.** Issuing a refund via the API returns `pending` from Stripe; the `charge.refunded` webhook later confirms. Your `RefundIssuedEvent` should be published from the webhook path, not the API request path. The API request just records intent; the webhook records completion.
5. **`PaymentProviderHealthCheck` calls live API endpoints.** On Fly's free auto-stop machines, `/health/ready` runs frequently — every readiness probe burns a Stripe + PayPal API call. Cache the result for 30s in the health check itself (the monolith's version probably does this; verify and preserve). Otherwise expect rate-limit headers from the providers in production.
6. **Webhook router ordering matters.** Some webhooks are version-tagged in headers; route by the `User-Agent` or signature header (`Stripe-Signature`, `PAYPAL-TRANSMISSION-SIG`), not by URL path. The monolith does this. If you route by URL path only, an attacker could spoof a Stripe webhook on `/webhooks/paypal` and bypass signature checks.
7. **Subscription controller authorization.** The endpoints handle money. JWT alone isn't enough — add a permission claim check (`payments:write` or similar) and rate-limit aggressively. Look at how the monolith's `SubscriptionController` decorates its actions and replicate.
8. **Sandbox vs Live config.** PayPal has a `Sandbox` and `Live` mode that select between `api-m.sandbox.paypal.com` and `api-m.paypal.com`. `PayPalEndpoints` gates this off `PayPalOptions.Environment`. Make sure the bootstrap script defaults to `Sandbox` so a misconfigured prod deploy doesn't accidentally hit live PayPal.
9. **Don't break phase 1 paths.** All Stripe checkout + webhook flows must still pass after phase 2 lands. Run phase 1's `Payments.Unit` and `CheckoutOrchestrator.Integration` suites as part of every PR's verification — they're your regression net.

## What you DO NOT need to do

- Don't touch `Orders`, `Catalog`, `BFF`, `Identity`, or `CheckoutOrchestrator` business logic. The only edits outside `Payments` are: adding new event records to `Contracts`, optionally adding HttpClients to BFF for subscription proxying.
- Don't reimplement the saga state machine.
- Don't add `IOrderRepository` to Payments. Ever.
- Don't write a README or migration doc unless explicitly asked. The brief itself is the doc.
- Don't introduce new infrastructure dependencies (no new MassTransit transports, no new caching providers). Reuse Redis, RabbitMQ, Postgres that the platform already has.

## How to verify locally

```bash
# Build everything
dotnet build HaworksPlatform.sln -c Release

# Per-suite tests (run in order; if any fails, stop)
dotnet test tests/Payments.Unit
dotnet test tests/Payments.Integration
dotnet test tests/Payments.Contract
dotnet test tests/CheckoutOrchestrator.Integration

# E2E sanity — PayPal path
PaymentProviders__Active=PayPal dotnet run --project deploy/aspire &
# Use PayPal sandbox account creds (configured via Vault dev seed or .env.local)
curl -X POST http://localhost:5050/api/checkouts/initiate \
  -H "Content-Type: application/json" \
  -H "X-Idempotency-Key: $(uuidgen)" \
  -d '{"orderId":"...","items":[...],"totalAmount":99.99,"currency":"USD"}'
# Complete checkout in browser via the returned PayPal approval URL.
# Poll GET /api/checkouts/{sagaId} until State=="Completed".

# E2E sanity — subscription
curl -X POST http://localhost:5050/api/subscriptions \
  -H "Authorization: Bearer ${JWT}" \
  -H "Content-Type: application/json" \
  -d '{"planId":"plan_demo_monthly"}'
# Complete in browser. Check haworks-payments DB; subscription row should exist.
# Poll RabbitMQ management UI for SubscriptionStartedEvent on the contracts queue.

# Health check
curl http://localhost:5050/health/ready
# Expect 200 with both providers showing healthy.
# Invalidate STRIPE_SECRET_KEY locally; expect 503 with Stripe component unhealthy.
```

When PR 6 through PR 10 are merged with green tests on every step, the payments port is complete. Hand back commit hashes per PR.
