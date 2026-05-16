# B2 — Synchronous reservation HTTP endpoints (was C2)

## Goal

Add the ADR-004 sync reservation flow now that B1's lifecycle is in place: `POST /api/checkout/reservations` (201/409 fail-fast) and `POST /api/checkout/reservations/{id}/confirm` (server-issued OrderId), plus BFF passthrough.

## Phase / blocks-on

Phase B. **Blocks-on:** A1, A2, A3, A4, **B1** all merged into `main`. Parallel-safe with B3.

## Inputs (read in order)

1. `docs/agent-briefs/platform/README.md`.
2. `docs/agent-briefs/platform-completion-spec.md` — Phase B "B2".
3. `docs/agent-briefs/checkout/C2-sync-reservation-endpoints.md` — the original (deferred) brief. Read it; this is its successor with B1's lifecycle now available. The Deliverable + Acceptance sections are still mostly correct — just substitute `StockReservation` for `OrderStockReservation` and use the new `Confirm`/`Expire` methods.
4. `src/Catalog/Catalog.Domain/StockReservation.cs` (B1).
5. `src/Catalog/Catalog.Domain/Interfaces/IReservationRepository.cs` (B1) — note the `CreateReservationAsync(...)` and `GetByIdTrackedAsync(...)` methods you'll call.
6. `src/Catalog/Catalog.Application/Interfaces/IStockService.cs` (B1) — note the new `ReleaseStockAsync(IEnumerable<StockReservationItem>, CancellationToken)` overload.
7. `src/BuildingBlocks/Extensions/HttpContextExtensions.cs` (A1) — for `GetForwardedUserId()`.

## Deliverable

Same as the C2 brief (Deliverable section), with these substitutions:

- Use `HttpContext.GetForwardedUserId()` to read the user id (A4-pattern).
- Use `IReservationRepository.CreateReservationAsync(...)` for the create path.
- Use `reservation.Confirm(orderId, sagaId)` (returns bool) for the confirm path; map the false return to `Conflict` (`Reservation.InvalidState`) when status is not `Pending`, or `Gone` when expired.
- Tests live in `tests/Catalog.Integration/ReservationEndpointTests.cs` with `[Collection("Catalog Integration")]`.

Authorization: 
- `POST /api/checkout/reservations` — anonymous-allowed (uses guest user id constant if `GetForwardedUserId()` returns null). Or `[Authorize]` if guest reservations aren't supported by the platform yet — check existing checkout code.
- `POST /api/checkout/reservations/{id}/confirm` — `[Authorize]`. Email claim required for the OrderId allocation per ADR-004 phase 4 — read from `HttpContext` claims; if missing, 400.

BFF passthrough — `src/BffWeb/BffWeb.Api/Controllers/ReservationsController.cs` mirrors the existing `SubscriptionsController.cs` style. The forwarding handler from A3 already sets `X-User-Id` automatically — no extra wiring needed.

## Acceptance

```bash
dotnet build HaworksPlatform.sln -c Release
dotnet test tests/Catalog.Unit -c Release
dotnet test tests/Catalog.Integration -c Release --filter "FullyQualifiedName~ReservationEndpointTests"
dotnet test tests/Catalog.Integration -c Release   # full suite — no regressions
```

All green. The 6 cases from the C2 brief (`CreateReservation_returns_201_when_stock_available` etc.) all pass.

## Hard stops

- Do **NOT** modify `src/Catalog/Catalog.Domain/` (B1 owns it).
- Do **NOT** modify the sweeper (B3's territory).
- Do **NOT** add `GetReservationByIdAsync` or any read endpoint — out of scope (the sync flow only has create + confirm).
- Do **NOT** push, deploy, force, amend, rebase, or open PRs.

## Done-report

Standard format. Confirm:
- Both endpoints work, return the right status codes per ADR-004 (201/409/410/404).
- BFF passthrough is in place + has `[Authorize]` on confirm.
- All 6 endpoint integration tests pass.
- `[Collection("Catalog Integration")]` is on the new test class.
