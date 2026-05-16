# A3 — BFF: register UserIdentityForwardingHandler + add [Authorize] to user-aware routes

## Goal

The BFF validates JWTs (via the same `AddPlatformAuthentication` helper from A1), and every named HttpClient that talks to a backend service runs the `UserIdentityForwardingHandler` so backend services receive `X-User-Id` as a trusted header.

## Phase / blocks-on

Phase A. **Blocks-on:** A1 merged into `main`. Parallel-safe with A2 and A4.

## Inputs (read in order)

1. `docs/agent-briefs/platform/README.md`.
2. `docs/agent-briefs/platform-completion-spec.md` — Phase A "BFF user-id propagation".
3. `src/BuildingBlocks/Extensions/AuthenticationExtensions.cs` (A1).
4. `src/BuildingBlocks/Authentication/UserIdentityForwardingHandler.cs` (A1).
5. `src/BffWeb/BffWeb.Api/Program.cs` — read the `foreach (var name in new[] { BackendClients.… })` block where typed HttpClients are registered. You'll add the handler there.
6. `src/BffWeb/BffWeb.Api/BackendClients.cs` — list of named clients you'll register the handler on.
7. `src/BffWeb/BffWeb.Api/Controllers/SubscriptionsController.cs` — the BFF passthrough that gets `[Authorize]` added.

## Deliverable

### `Program.cs` edits

After `builder.AddServiceDefaults()` and before the typed-HttpClient loop:

```csharp
builder.Services.AddPlatformAuthentication(builder.Configuration);
builder.Services.AddHttpContextAccessor();
builder.Services.AddTransient<UserIdentityForwardingHandler>();
```

In the existing `foreach (var name in new[] { … })` typed-client loop, add `.AddHttpMessageHandler<UserIdentityForwardingHandler>()` to **each** client's chain. Position: after the chaos-injection handler, before the `UpstreamInstanceCaptureHandler` so the X-User-Id is set before instance capture.

Also add the same handler to the `CatalogDemo` named client registration further down in `Program.cs`.

### `[Authorize]` on BFF passthrough controllers

Add `[Authorize]` to `BffWeb.Api/Controllers/SubscriptionsController.cs` (currently anonymous; the C1 work added the controller without auth).

Other BFF controllers that should require auth: leave them as-is unless explicitly told. **Do not** decide for the Checkout / Demo / System / Chaos controllers without a follow-up brief.

### Tests

`tests/BffWeb.Integration/UserIdentityForwardingTests.cs`:

- `Forwarding_handler_sets_X_User_Id_on_outbound_request_for_authenticated_user` — use a `WireMock`-style stub backend (or the existing pattern in BFF tests, mirror it), hit a BFF route, assert the upstream request had the header.
- `Forwarding_handler_omits_header_for_anonymous_request` — same setup, anonymous, assert no header.

If the BFF integration suite uses a different pattern (e.g. WebApplicationFactory + stubbed HttpClient), match that.

## Acceptance

```bash
dotnet build HaworksPlatform.sln -c Release
dotnet test tests/BffWeb.Integration -c Release
dotnet test tests/BffWeb.Unit -c Release   # if it exists
```

All green. The 2 new forwarding tests pass.

## Hard stops

- Do **NOT** modify any backend-service `Program.cs` (A2's territory).
- Do **NOT** modify any backend controller (A4's territory).
- Do **NOT** modify `BuildingBlocks` files (A1's territory). Use them as-is.
- Do **NOT** add `[Authorize]` to non-subscription BFF controllers without an explicit follow-up.
- Do **NOT** remove or modify the existing `ChaosFaultInjectionHandler` or `UpstreamInstanceCaptureHandler` — slot the new handler alongside them.
- Do **NOT** push, deploy, force, amend, rebase, or open PRs.

## Done-report

Standard format. Confirm:
- `UserIdentityForwardingHandler` registered as transient.
- Handler appears in every named-HttpClient chain in `Program.cs`.
- `[Authorize]` added to BFF `SubscriptionsController`.
- 2 new tests pass.
