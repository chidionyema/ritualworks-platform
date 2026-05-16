# B3 — ReservationSweeperService (was C3)

## Goal

Add the periodic background job that releases expired stock reservations. Runs every minute, batches of 200, idempotent against double-release races. Now that B1's lifecycle is in place, this is a clean direct port of the monolith's sweeper.

## Phase / blocks-on

Phase B. **Blocks-on:** B1 merged into `main`. Parallel-safe with B2.

## Inputs (read in order)

1. `docs/agent-briefs/platform/README.md`.
2. `docs/agent-briefs/platform-completion-spec.md` — Phase B "B3".
3. `docs/agent-briefs/checkout/C3-reservation-sweeper.md` — the original (deferred) brief. Read it; this is its successor. Deliverable + Acceptance still mostly correct — just substitute `StockReservation` for `OrderStockReservation`, use `reservation.Expire()` (the B1 method), and call `IStockService.ReleaseStockAsync(items, ct)` (the new B1 overload).
4. `src/Catalog/Catalog.Domain/StockReservation.cs` (B1) — the `Expire()` method.
5. `src/Catalog/Catalog.Domain/Interfaces/IReservationRepository.cs` (B1) — the `ListExpiredAsync(now, batchSize, ct)` method you'll call.
6. `src/Catalog/Catalog.Application/Interfaces/IStockService.cs` (B1) — the `ReleaseStockAsync(IEnumerable<StockReservationItem>, CancellationToken)` overload.

## Deliverable

Same as the C3 brief (Deliverable section), with these substitutions:

- `src/Catalog/Catalog.Application/Interfaces/IReservationMetrics.cs` and `NullReservationMetrics` — exactly as C3 specified.
- `src/Catalog/Catalog.Application/Options/ReservationSweeperOptions.cs` — same.
- `src/Catalog/Catalog.Infrastructure/BackgroundServices/ReservationSweeperService.cs` — uses `IReservationRepository.ListExpiredAsync(...)` + `reservation.Expire()` + `IStockService.ReleaseStockAsync(items, ct)`. Order-of-operations is load-bearing (Expire first, then ReleaseStock — partial failure leaves Pending, retried next sweep).
- DI registration in `src/Catalog/Catalog.Infrastructure/DependencyInjection.cs` — exactly as C3 specified, behind `!env.IsEnvironment("Test")`.

Tests live in `tests/Catalog.Integration/ReservationSweeperTests.cs` with `[Collection("Catalog Integration")]`. Same 3 cases from the C3 brief.

## Acceptance

```bash
dotnet build HaworksPlatform.sln -c Release
dotnet test tests/Catalog.Integration -c Release --filter "FullyQualifiedName~ReservationSweeperTests"
dotnet test tests/Catalog.Integration -c Release   # full suite — no regressions
```

All green.

## Hard stops

- Do **NOT** modify any controller or API file in `src/Catalog/Catalog.Api/` (B2's territory).
- Do **NOT** modify `src/Catalog/Catalog.Domain/` (B1's territory).
- Do **NOT** add a real OpenTelemetry-backed `IReservationMetrics` — `NullReservationMetrics` is the deliberate v1 default.
- Do **NOT** add a sweep API endpoint ("force sweep now") — out of scope.
- Do **NOT** push, deploy, force, amend, rebase, or open PRs.

## Done-report

Standard format. Confirm:
- Sweeper registered ONLY when `!env.IsEnvironment("Test")`.
- Aggregate-transition-before-stock-release ordering preserved.
- All 3 sweeper integration tests pass.
- Catalog full integration suite still green.
