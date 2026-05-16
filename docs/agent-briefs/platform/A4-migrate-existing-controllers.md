# A4 — Migrate user-aware backend controllers to read X-User-Id

## Goal

Backend controllers that need a user id stop reading `User.FindFirstValue(...)` and start reading `HttpContext.GetForwardedUserId()` (the BFF-set header). `[Authorize]` still enforces "someone is authenticated"; the user identity comes from the trusted forwarded header.

## Phase / blocks-on

Phase A. **Blocks-on:** A1 merged into `main`. Parallel-safe with A2 and A3.

## Inputs (read in order)

1. `docs/agent-briefs/platform/README.md`.
2. `docs/agent-briefs/platform-completion-spec.md` — Phase A "Migrate existing `[Authorize]` controllers".
3. `src/BuildingBlocks/Extensions/HttpContextExtensions.cs` (A1) — the `GetForwardedUserId()` method you'll be calling.
4. `src/Payments/Payments.Api/Controllers/SubscriptionsController.cs` — your primary target. C1 wired this with `User.FindFirstValue(...)`; you swap it.
5. `src/Orders/Orders.Api/Controllers/OrdersController.cs` — has `[Authorize]`; check whether any handler reads user id.
6. `src/Catalog/Catalog.Api/Controllers/ProductReviewsController.cs` — has `[Authorize]`; check whether any handler reads user id.
7. `tests/Payments.Integration/SubscriptionEndpointTests.cs` — the existing tests; you'll need to adapt them so they send `X-User-Id` (the test fixture's auth handler sets a default, but the new code path reads from the header).

## Deliverable

### Migrate user-id reads

In every backend controller listed above (and any other backend controller that calls `User.FindFirstValue(ClaimTypes.NameIdentifier)` or `User.FindFirstValue("sub")`), replace:

```csharp
var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)
          ?? User.FindFirstValue("sub");
```

with:

```csharp
var userId = HttpContext.GetForwardedUserId();
```

If `userId` is null, return `Unauthorized()` — same as the previous logic. The `[Authorize]` attribute on the route already filters anonymous requests; the null check guards against a misconfigured BFF that fails to forward the header.

### Test fixture updates

Each test fixture that exercises an authenticated route must now also set the `X-User-Id` header on its test client. Two ways:

1. **Default header per call** — `_client.DefaultRequestHeaders.Add("X-User-Id", "test-user-1")` after `factory.CreateClient()`.
2. **Test-handler injection** — extend the existing `TestAuthenticationHandler` (in `BuildingBlocks.Testing`) to also set the header on every authenticated test request. Cleaner; preferred if it's a small change.

Option 2 is more maintainable. Pick it unless `TestAuthenticationHandler`'s shape doesn't accommodate it (then option 1 + a TODO).

### Existing test cases

Update existing test cases (e.g. `SubscriptionEndpointTests`) to not break: now the controller reads `X-User-Id`, so any test that asserted "user id was X" needs the header to be set. Most should still pass with the test handler's default.

Add one test per migrated controller: `Endpoint_returns_401_when_X_User_Id_header_missing` — sends a valid bearer token but no `X-User-Id`, asserts 401.

## Acceptance

```bash
dotnet build HaworksPlatform.sln -c Release
dotnet test tests/Payments.Integration -c Release
dotnet test tests/Orders.Integration -c Release
dotnet test tests/Catalog.Integration -c Release
```

All green. Existing tests pass; new "header missing → 401" tests pass.

## Hard stops

- Do **NOT** modify any backend `Program.cs` (A2's territory).
- Do **NOT** modify the BFF (A3's territory).
- Do **NOT** modify `BuildingBlocks` (A1's territory).
- Do **NOT** add new endpoints, only migrate the user-id read path.
- Do **NOT** invent new claim types or header names. Use exactly `X-User-Id` from `UserIdentityForwardingHandler.HeaderName`.
- Do **NOT** push, deploy, force, amend, rebase, or open PRs.

## Done-report

Standard format. Confirm:
- Every `User.FindFirstValue(NameIdentifier)`/`("sub")` in backend controllers now reads `HttpContext.GetForwardedUserId()`.
- Test fixtures send `X-User-Id` for authenticated calls.
- Header-missing-401 tests pass.
- List the controllers migrated (so the reviewer can spot any missed).
