# C3 — `ReservationSweeperService` (catalog-svc background hosted service)

## Goal

Add the periodic background job that releases expired stock reservations. Runs every minute, batches of 200, idempotent against double-release races. Without it, every abandoned cart leaks inventory until manual cleanup.

## Phase / blocks-on

Phase 1 (parallel with C1, C2, C4). Blocks-on: nothing.

## Inputs (read in order, all of them, before writing)

1. `docs/agent-briefs/checkout/README.md`.
2. `docs/agent-briefs/checkout-payments-gaps-spec.md` — pay attention to "Design decisions" → sweeper interval/batch/options.
3. `src/Catalog/Catalog.Domain/` — find the `StockReservation` aggregate. Confirm it has an `Expire()` method that guards on `Status == Pending && ExpiresAt < now` and transitions to `Expired`. If missing, file a blocker — adding domain methods is out of scope for this brief.
4. `src/Catalog/Catalog.Domain/Interfaces/` — find `IStockService` (or the equivalent — the monolith calls it that). Confirm it has a `ReleaseStockAsync(IEnumerable<StockReservationItem> items, CancellationToken)` method. If the only "release" path goes through events, that's fine — just use that path; flag in out-of-scope observations.
5. `src/Catalog/Catalog.Infrastructure/CatalogDbContext.cs` — confirm `DbSet<StockReservation>` exists.
6. `src/Catalog/Catalog.Infrastructure/DependencyInjection.cs` — you'll add **one line** here to register the hosted service. Read the whole file first to see where existing `Add*` calls are; preserve the surrounding structure.
7. `src/Payments/Payments.Application/Webhooks/PaymentAmountMismatchHandler.cs` (or any existing infrastructure-side application service) — style reference for the metrics interface.
8. `tests/Catalog.Integration/CatalogWebAppFactory.cs` and `CatalogFlowsTests.cs` — test fixture. Note the existing `[Collection("Catalog Integration")]`.

## Deliverable

### Application layer (small interface only)

- `src/Catalog/Catalog.Application/Interfaces/IReservationMetrics.cs`:

```csharp
public interface IReservationMetrics
{
    void RecordReservationExpiredBySweeper();
    void RecordReservationHoldDuration(TimeSpan duration, string terminalStatus);
}
```

- `src/Catalog/Catalog.Infrastructure/Metrics/NullReservationMetrics.cs` — a no-op implementation, registered as the default. The platform may add a real OTel-backed implementation later.

### Application options

- `src/Catalog/Catalog.Application/Options/ReservationSweeperOptions.cs`:

```csharp
public sealed class ReservationSweeperOptions
{
    public const string SectionName = "Reservations:Sweeper";
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromMinutes(1);
    public int BatchSize { get; set; } = 200;
}
```

### Infrastructure layer (the hosted service)

`src/Catalog/Catalog.Infrastructure/BackgroundServices/ReservationSweeperService.cs`:

- Inherits `BackgroundService`.
- Constructor takes `IServiceScopeFactory`, `IOptions<ReservationSweeperOptions>`, `ILogger<>`. (Background services must scope per-iteration; do **not** inject `CatalogDbContext` directly.)
- `ExecuteAsync` loop:
  - On each tick: scope, query Postgres for `Pending && ExpiresAt < UtcNow`, ordered by `ExpiresAt`, top `BatchSize`.
  - For each candidate: try `reservation.Expire()` (catches `InvalidOperationException` from the aggregate guard — log at Debug, continue). On success, `await stockService.ReleaseStockAsync(items, ct)`. Record metrics.
  - `db.SaveChangesAsync(ct)` once at the end of the batch.
  - Outer try/catch around the whole sweep — never let the loop die from a single transient DB error.
  - `Task.Delay(options.SweepInterval, stoppingToken)` between sweeps (handle `OperationCanceledException` to exit cleanly).
- Expose `internal Task<int> SweepOnceAsync(CancellationToken ct)` for tests — returns the number of reservations actually expired.

Mirror the monolith's `src/Infrastructure/BackgroundServices/ReservationSweeperService.cs` line-by-line for the tricky behaviour (aggregate-transition-before-stock-release ordering is the load-bearing piece — partial failure must leave the reservation Pending, not half-released).

### DI registration (the **only** modification to existing infra DI)

In `src/Catalog/Catalog.Infrastructure/DependencyInjection.cs`, add (and only this):

```csharp
services.AddOptions<ReservationSweeperOptions>()
    .Bind(configuration.GetSection(ReservationSweeperOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

services.AddSingleton<IReservationMetrics, NullReservationMetrics>();

if (!env.IsEnvironment("Test"))
{
    services.AddHostedService<ReservationSweeperService>();
}
```

Skip in Test so integration tests can drive the sweeper deterministically via `SweepOnceAsync` instead of waiting for the timer.

### Tests

`tests/Catalog.Integration/ReservationSweeperTests.cs` — `[Collection("Catalog Integration")]`:

- `SweepOnce_expires_reservations_with_past_deadline` — seed 3 reservations (1 pending+expired, 1 pending+future, 1 already-confirmed). Resolve `ReservationSweeperService` from DI, call `SweepOnceAsync(CancellationToken.None)`, assert it returns `1` and only the expected row moved to `Expired`.
- `SweepOnce_caps_at_batch_size` — seed 250 expired-pending, configure batch size 100, assert sweep processes 100. (Mutates options via test-only configuration; mirror PaymentsWebAppFactory's env-var-before-host-build pattern if the options need to change between tests.)
- `SweepOnce_skips_already_confirmed_reservations` — guard regression for the aggregate's `Expire()` invariant.

## Acceptance

```bash
dotnet build HaworksPlatform.sln -c Release
dotnet test tests/Catalog.Integration -c Release --filter "FullyQualifiedName~ReservationSweeperTests"
dotnet test tests/Catalog.Integration -c Release   # full suite — no regressions
```

All green. The sweeper hosted service is registered behind `!env.IsEnvironment("Test")` (tests must NOT see the timer fire on its own).

## Hard stops

- Do **not** modify any controller or API file in `src/Catalog/Catalog.Api/`. C2 owns those edits this phase.
- Do **not** modify the `StockReservation` aggregate or any domain interface. Use only the existing public API.
- Do **not** modify any other service (`src/Payments/`, `src/Orders/`, BFF, etc.).
- Do **not** add a real OpenTelemetry-backed `IReservationMetrics` implementation — `NullReservationMetrics` is the deliberate v1 default. The OTel cross-service initiative comes later.
- Do **not** add a sweep API endpoint ("force sweep now"). Out of scope.

## Done-report

Standard format. Specifically confirm:
- The sweeper is registered ONLY when `!env.IsEnvironment("Test")`.
- Aggregate-transition-before-stock-release ordering is preserved (a half-released reservation must be impossible).
- Catalog full integration suite still green.
