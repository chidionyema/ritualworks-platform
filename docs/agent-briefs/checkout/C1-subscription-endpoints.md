# C1 — Subscription HTTP endpoints (payments-svc + BFF passthrough)

## Goal

Expose the already-migrated subscription backend services through HTTP. Add `GET /api/subscriptions/status` and `POST /api/subscriptions/create-checkout-session` on `payments-svc`, plus matching BFF passthrough routes. JWT-bearer authentication required.

## Phase / blocks-on

Phase 1 (parallel with C2, C3, C4). Blocks-on: nothing — backend services already exist.

## Inputs (read in order, all of them, before writing)

1. `docs/agent-briefs/checkout/README.md` — protocol you're working under.
2. `docs/agent-briefs/checkout-payments-gaps-spec.md` — full spec; pay attention to "Architectural placement" and "Design decisions".
3. `src/Payments/Payments.Application/Interfaces/ISubscriptionManager.cs` — primary contract you'll be wrapping.
4. `src/Payments/Payments.Application/Interfaces/ISubscriptionService.cs` — read contract.
5. `src/Payments/Payments.Domain/Subscription.cs` — entity shape (status enum, plan id, expiry semantics).
6. `src/Payments/Payments.Application/Interfaces/IPaymentRepository.cs` — for any subscription-row queries.
7. `src/Payments/Payments.Api/Controllers/WebhooksController.cs` — controller style template (logger, route attributes, auth).
8. `src/Payments/Payments.Api/Program.cs` — confirm `AddControllers()` is wired (it is) and JWT auth is configured. If not configured, file a blocker — do not add auth setup yourself.
9. `src/BffWeb/BffWeb.Api/Controllers/SearchController.cs` — your **exact** template for the BFF passthrough.
10. `src/BffWeb/BffWeb.Api/BackendClients.cs` — confirm `Payments` constant exists. It does.
11. `tests/Payments.Integration/PaymentsWebAppFactory.cs` and one existing test class (e.g. `WebhookFlowsTests.cs`) — fixture pattern.

If `ISubscriptionManager.HandleSubscriptionEventAsync(...)` is the only method exposed and there's no `CreateSubscriptionCheckoutAsync` method or equivalent — file a blocker. Do not invent business logic; the brief assumes the create-checkout path exists. If it doesn't, the gap is bigger than just "add a controller" and the user needs to know.

## Deliverable

### Application layer (new files)

- `src/Payments/Payments.Application/DTOs/Subscriptions/SubscriptionStatusDto.cs` — record `(bool IsSubscribed, string? PlanId, DateTime? ExpiresAt)`.
- `src/Payments/Payments.Application/DTOs/Subscriptions/CreateSubscriptionCheckoutResultDto.cs` — record `(string SessionId, string CheckoutUrl)`.
- `src/Payments/Payments.Application/Queries/Subscriptions/GetSubscriptionStatusQuery.cs` — `record GetSubscriptionStatusQuery(string UserId) : IRequest<Result<SubscriptionStatusDto>>` + handler. Reads from `IPaymentRepository` (or `ISubscriptionService` if it exposes a "for user" query). No throwing on "no subscription" — return `IsSubscribed=false`.
- `src/Payments/Payments.Application/Commands/Subscriptions/CreateSubscriptionCheckoutCommand.cs` — `record CreateSubscriptionCheckoutCommand(string UserId, string PriceId, decimal Amount, string? RedirectPath) : IRequest<Result<CreateSubscriptionCheckoutResultDto>>` + handler. Calls into the existing subscription manager / checkout-session service; do not duplicate logic.
- Validators for both, sibling-file convention (`*Validator.cs`).

### API layer (new file)

`src/Payments/Payments.Api/Controllers/SubscriptionsController.cs`:

```csharp
[ApiController]
[Route("api/subscriptions")]
[Authorize]
public sealed class SubscriptionsController(IMediator mediator) : ControllerBase
{
    [HttpGet("status")]
    public async Task<IActionResult> GetStatus(CancellationToken ct) { … }

    [HttpPost("create-checkout-session")]
    public async Task<IActionResult> CreateCheckoutSession(
        [FromBody] CreateSubscriptionCheckoutRequest body, CancellationToken ct) { … }
}
```

User id pulled from `User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")` (matches the monolith's JwtBearer claim-mapping handling). Anonymous → `Unauthorized`. The request DTO `CreateSubscriptionCheckoutRequest` lives next to the controller.

### BFF passthrough (new file)

`src/BffWeb/BffWeb.Api/Controllers/SubscriptionsController.cs` — exact same shape as the existing `SearchController.cs`. Forwards `GET /api/subscriptions/status` → `payments-svc:/api/subscriptions/status` and `POST /api/subscriptions/create-checkout-session` → `payments-svc:/api/subscriptions/create-checkout-session`. Uses `IHttpClientFactory.CreateClient(BackendClients.Payments)`. Preserves `Authorization` header on the upstream call.

### Tests

`tests/Payments.Unit/Subscriptions/GetSubscriptionStatusHandlerTests.cs` and `CreateSubscriptionCheckoutHandlerTests.cs` — handler unit tests with mocked repository / subscription manager. 3-4 cases each (happy path, no subscription, manager throws, validation failure).

`tests/Payments.Integration/SubscriptionEndpointTests.cs` — uses `[Collection("Payments Integration")]`, exercises both endpoints over HTTP through the existing `PaymentsWebAppFactory`. Stripe is already mocked in the fixture; rely on the `ISubscriptionManager` mock surface. Cases:
- `Status_returns_401_when_unauthenticated`
- `Status_returns_200_with_dto_when_subscription_exists`
- `Status_returns_200_with_IsSubscribed_false_when_no_subscription`
- `CreateCheckoutSession_returns_200_with_session_id`
- `CreateCheckoutSession_returns_400_when_amount_invalid`

## Acceptance

```bash
dotnet build HaworksPlatform.sln -c Release
dotnet test tests/Payments.Unit -c Release
dotnet test tests/Payments.Integration -c Release --filter "FullyQualifiedName~SubscriptionEndpointTests"
dotnet test tests/Payments.Integration -c Release   # full suite — no regressions
```

All green. New unit + integration tests pass.

## Hard stops

- Do **not** modify any file in `src/Catalog/`, `src/Orders/`, or any other service.
- Do **not** modify `src/Payments/Payments.Infrastructure/DependencyInjection.cs` — MediatR scans the application assembly automatically.
- Do **not** invent subscription business logic. If a missing service is needed, file a blocker.
- Do **not** add a subscription **cancellation** endpoint. Out of scope.
- Do **not** add CSRF / anti-forgery setup. The platform's BFF already handles that globally.

## Done-report

Standard format. Specifically confirm:
- The user-id claim lookup matches the monolith pattern (`NameIdentifier` then `sub` fallback).
- BFF passthrough preserves `Authorization` header.
- All 4 acceptance commands pass.
