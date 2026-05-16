# A2 — Per-service JWT wiring (catalog, orders, payments, search, checkout-orchestrator)

## Goal

Every backend service that has `[Authorize]`-decorated controllers (or might have, soon) registers JWT validation via the new `AddPlatformAuthentication` helper from A1. Add a single 401-without-token integration test per service to lock the behaviour in.

## Phase / blocks-on

Phase A. **Blocks-on:** A1 merged into `main`.

## Inputs (read in order)

1. `docs/agent-briefs/platform/README.md`.
2. `docs/agent-briefs/platform-completion-spec.md` — Phase A "Per-service wiring" + "Tests".
3. `src/BuildingBlocks/Extensions/AuthenticationExtensions.cs` (from A1).
4. `src/Payments/Payments.Api/Program.cs`, `src/Catalog/Catalog.Api/Program.cs`, `src/Orders/Orders.Api/Program.cs`, `src/Search/Search.Api/Program.cs`, `src/CheckoutOrchestrator/CheckoutOrchestrator.Api/Program.cs` — the five backend `Program.cs` files you'll edit.
5. One existing integration test per service that exercises a controller route (e.g. `tests/Payments.Integration/SubscriptionEndpointTests.cs`) — copy its style for the 401 cases.

## Deliverable

### Per-service `Program.cs` edit (5 services, 1 line each + a using)

For each of `Payments.Api`, `Catalog.Api`, `Orders.Api`, `Search.Api`, `CheckoutOrchestrator.Api`:

Add — after `builder.Services.AddInfrastructure(...)` and before `builder.Services.AddControllers()`:

```csharp
builder.Services.AddPlatformAuthentication(builder.Configuration);
builder.Services.AddHttpContextAccessor();
```

`identity-svc` is the JWT issuer — it already has its own `AddAuthentication`. **Do not touch its Program.cs.**

`bff-web` is A3's territory.

### Per-service Fly toml — JWT secrets

Each backend service needs `Jwt:Issuer`, `Jwt:Audience`, `Jwt:SigningKeyPem` available at runtime. Identity-svc already gets these via `bootstrap.sh`. Confirm that other services either:
- Already inherit them (check `bootstrap.sh` per-service secret loop), OR
- Need a one-line addition to `bootstrap.sh` to also stage them.

If `bootstrap.sh` doesn't currently stage the JWT secrets on backend services, **add them**. Mirror the Identity stage block exactly. The keys are the same across all services — Identity signs, others validate.

### One 401 test per service

For each service's integration suite, add one test that hits an `[Authorize]` route without a token and asserts 401. Where the suite has no `[Authorize]` route yet, create the test against any `[Authorize]` route the service exposes (Catalog has `ProductReviewsController`, Orders has `OrdersController`, Payments has `SubscriptionsController`, etc.). For Search/Checkout-orchestrator, if no such route exists yet, file a blocker — don't add `[Authorize]` to a route just to test it.

Test name: `<RouteName>_returns_401_when_called_without_bearer_token`.

The test fixture's `TestAuthenticationHandler` setup already provides authenticated test calls. The 401 case is just an HTTP call without the test handler's headers — verify by reading existing `PaymentsWebAppFactory.cs`'s test-auth wiring.

## Acceptance

```bash
dotnet build HaworksPlatform.sln -c Release
dotnet test tests/Payments.Integration -c Release
dotnet test tests/Catalog.Integration -c Release
dotnet test tests/Orders.Integration -c Release
dotnet test tests/Search.Integration -c Release
dotnet test tests/CheckoutOrchestrator.Integration -c Release   # if it exists; else skip
```

All green. The 5 new 401 tests are listed as passed.

## Hard stops

- Do **NOT** modify `bff-web` (A3's territory).
- Do **NOT** modify `identity-svc` Program.cs — it already wires its own auth as the issuer.
- Do **NOT** modify `BuildingBlocks/Extensions/AuthenticationExtensions.cs` — A1 owns it.
- Do **NOT** modify any controller — A4 does that.
- Do **NOT** add `[Authorize]` to a route just so a test has something to hit. File a blocker if a service has no auth-decorated route.
- Do **NOT** push, deploy, force, amend, rebase, or open PRs.

## Done-report

Standard format. Confirm:
- All 5 backend `Program.cs` files have `AddPlatformAuthentication(...)` + `AddHttpContextAccessor()`.
- `bootstrap.sh` stages the JWT secrets on all 5 services (or already did).
- 5 new 401 integration tests pass.
