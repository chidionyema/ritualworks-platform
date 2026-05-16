# CheckoutSaga — cheap-wins refactor

**Mode:** `modify-existing-service`. Wave should auto-detect `WAVE_MODE=modify`.

**Scope:** three tightening fixes identified during the saga audit. Each is bounded (~1h), independent, and parallelizable. None changes the saga's external contract — same events, same states, same outcomes — just tightens observability + structure.

**Why this matters:** see audit findings in chat thread on 2026-05-10. The saga is exemplary overall; these address the three named "cheap-win" concerns without disrupting the working pattern.

## Tracks — 3 parallel, ~1h each

### Track T1: structured `FailureReason` enum + EF migration

**Files you own (exclusive):**
- `src/CheckoutOrchestrator/CheckoutOrchestrator.Domain/CheckoutFailureCategory.cs` (new)
- `src/CheckoutOrchestrator/CheckoutOrchestrator.Domain/CheckoutSagaState.cs` (add field + property)
- `src/CheckoutOrchestrator/CheckoutOrchestrator.Infrastructure/CheckoutDbContext.cs` (column mapping for the new field)
- `src/CheckoutOrchestrator/CheckoutOrchestrator.Infrastructure/Migrations/<ts>_AddCheckoutFailureCategory.*` (new EF migration files)
- `src/CheckoutOrchestrator/CheckoutOrchestrator.Application/Sagas/CheckoutSaga.cs` (set the new field on every compensation `.Then(...)` block — five places)
- `tests/CheckoutOrchestrator.Unit/SagaFailureCategoryTests.cs` (new unit test)

**Files you may NOT touch:**
- Any other saga state property
- The state transitions / `When(...)` / `TransitionTo(...)` ordering
- The existing `FailureReason` string field — preserve it for back-compat

**Reference to mirror:** `src/Orders/Orders.Domain/Order.cs` for the enum-on-aggregate pattern (Order.OrderStatus is similar — `Created`, `Paid`, `Cancelled`, etc.).

**NuGet (if any):** none

**Done:** `dotnet test tests/CheckoutOrchestrator.Unit/CheckoutOrchestrator.Unit.csproj -c Release --nologo --filter "FullyQualifiedName~SagaFailureCategory"` exits 0 + the EF migration applies cleanly against a fresh `checkout` database.

#### Work plan
1. Define `public enum CheckoutFailureCategory { None=0, StockReservationFailed, PaymentSessionFailed, PaymentSessionFailedPostSession, PaymentExpired, PaymentAmountMismatch }`.
2. Add `public CheckoutFailureCategory FailureCategory { get; set; }` to `CheckoutSagaState` (default `None`).
3. Map the column in `CheckoutDbContext.OnModelCreating` — store as `int` not `string` (smaller, indexed).
4. Generate migration: `dotnet ef migrations add AddCheckoutFailureCategory -p src/CheckoutOrchestrator/CheckoutOrchestrator.Infrastructure -s src/CheckoutOrchestrator/CheckoutOrchestrator.Api`.
5. In `CheckoutSaga.cs`, in EVERY existing `.Then(ctx => { ... ctx.Saga.FailureReason = ...; })` block where `FailureReason` is set, ALSO set `ctx.Saga.FailureCategory = CheckoutFailureCategory.<matching>`. Five locations: `When(StockReservationFailed)`, two `When(PaymentSessionFailed)` (Initiated and ReadyForPayment), two `When(PaymentExpirySchedule.Received)`, and `When(PaymentAmountMismatch)`.
6. Keep `FailureReason` string populated as today (no breaking change to existing consumers/observability).
7. Unit test asserts that for each failure-event injected against an in-memory state machine, the resulting `FailureCategory` matches the expected enum value AND `FailureReason` is still populated.

### Track T2: telemetry on `DeserializeItems` empty/malformed paths

**Files you own (exclusive):**
- `src/CheckoutOrchestrator/CheckoutOrchestrator.Application/Sagas/CheckoutSaga.cs` (modify only the `DeserializeItems` method + its call sites — wrap with telemetry)
- `tests/CheckoutOrchestrator.Unit/SagaDeserializeItemsTests.cs` (new)

**Files you may NOT touch:**
- Any state transition logic — only the `DeserializeItems` private method body and its 3 call sites in compensation `PublishAsync` chains
- `CheckoutSagaState.cs`
- The DI / `CheckoutActivities.Source` definition itself (just use it)

**Reference to mirror:** the existing `EmitCompensateSpan` static helper in the same `CheckoutSaga.cs` file — same pattern (StartActivity + SetTag + dispose).

**NuGet (if any):** none

**Done:** `dotnet test tests/CheckoutOrchestrator.Unit/CheckoutOrchestrator.Unit.csproj -c Release --nologo --filter "FullyQualifiedName~SagaDeserializeItems"` exits 0.

#### Work plan
1. Change `DeserializeItems` signature to accept `(Guid sagaId, Guid orderId, string? json)` so it can tag the span with correlation IDs.
2. Branch on three failure modes inside the method, each emitting a discrete activity (instantly disposed, like `EmitCompensateSpan`):
   - `checkout.saga.deserialize_items.empty_input` — when `json` is null/empty
   - `checkout.saga.deserialize_items.empty_after_parse` — when JSON parses but yields zero items
   - `checkout.saga.deserialize_items.malformed` — when `JsonSerializer.Deserialize` throws
   Each activity sets `saga.id`, `order.id`, and (for the latter two) `json.length`. Malformed adds `exception.message`.
3. Update the three call sites in `CheckoutSaga.cs` to pass `ctx.Saga.CorrelationId` and `ctx.Saga.OrderId`. They're all in compensation `.PublishAsync(ctx => ... DeserializeItems(...))` blocks.
4. Unit test feeds canned inputs (null, "", "[]", "garbage", a well-formed array) and asserts the returned list shape + that the expected activity name appears in a recording `ActivityListener`.

### Track T3: `PaymentExpiryWatcher` integration test

**Files you own (exclusive):**
- `tests/CheckoutOrchestrator.Integration/PaymentExpiryWatcherTests.cs` (new)
- `tests/CheckoutOrchestrator.Integration/PaymentExpiryWatcherFixture.cs` (new, mirrors `SagaCompensationFixture` pattern)

**Files you may NOT touch:**
- `src/CheckoutOrchestrator/CheckoutOrchestrator.Infrastructure/Workers/PaymentExpiryWatcher.cs` — already correct; the test exercises it as-is
- Any other test file under `tests/CheckoutOrchestrator.Integration/`

**Reference to mirror:** `tests/CheckoutOrchestrator.Integration/SagaCompensationChaosTests.cs` + its `SagaCompensationFixture` — proven pattern for Testcontainers-backed saga integration tests in this codebase.

**NuGet (if any):** none — `Testcontainers.PostgreSql`, `MassTransit.TestFramework`, `Microsoft.AspNetCore.Mvc.Testing` already present in `CheckoutOrchestrator.Integration.csproj`.

**Done:** `dotnet test tests/CheckoutOrchestrator.Integration/CheckoutOrchestrator.Integration.csproj -c Release --nologo --filter "FullyQualifiedName~PaymentExpiryWatcher"` exits 0.

#### Work plan

1. **Fixture (`PaymentExpiryWatcherFixture.cs`)**: copy the shape of `SagaCompensationFixture` but configure MassTransit with `AddInMemoryBus()` (no broker) — this means the broker-side scheduler can't fire, simulating the production failure mode the watcher exists to mitigate. Override `PaymentExpiryWatcher`'s `ExpiryDeadline` + `PollInterval` to short values (5s deadline, 1s poll) so the test completes in <30s.

2. **Test: `Watcher_publishes_PaymentExpired_when_scheduler_misses_deadline`**:
   - Seed a `CheckoutSagaState` row directly into the DB with `CurrentState = "StockReservedState"`, `CreatedAt = now - 10s` (past the 5s deadline), populated `ReservedItemsJson`.
   - Start the host (PaymentExpiryWatcher is registered).
   - Wait up to 15s for: (a) a `PaymentExpiredEvent` published on the bus, (b) the saga's `CurrentState` transitioned to `Abandoned`, (c) a `StockReleaseRequestedEvent` published.
   - Assert all three.

3. **Test: `Watcher_no_op_when_scheduler_already_completed_saga`**:
   - Seed a saga with `CurrentState = "Abandoned"`, `CreatedAt = now - 10s`.
   - Start host, wait 3 seconds (long enough for one poll tick).
   - Assert no `PaymentExpiredEvent` was published — the watcher's WHERE clause must exclude finalized sagas.

4. **Test: `Watcher_respects_MaxPublishesPerTick`**:
   - Seed 60 sagas all past deadline.
   - Start host, wait one poll cycle.
   - Assert exactly 50 `PaymentExpiredEvent`s published in the first tick (the configured cap), and the rest on the second tick.

## Universal rules

### File-scope discipline
Each track owns its listed files exclusively. No track edits another track's files. Track T1 and T2 both modify `CheckoutSaga.cs` — but T1 only inside the five `When(...).Then(...)` blocks for FailureCategory assignment, and T2 only inside `DeserializeItems` + its three call sites. **NO line overlap.** If you find yourself needing to touch a line the other track owns, stop and flag a brief defect.

### Build verify per file group
```bash
dotnet build "$WT/src/CheckoutOrchestrator" --nologo --verbosity quiet
```
Must exit 0 before every commit.

### Push cadence
Per file group (domain change → migration → saga changes → tests) commit + push immediately. Not "one big commit per track."

## Anti-stuck

1. **Migration won't apply locally** → check `tests/CheckoutOrchestrator.Integration/SagaCompensationFixture.cs` for how the test fixture brings up a fresh schema; the migration must apply there as a precondition.
2. **`When(...).Then(...)` accepts only one Then in the existing fluent chain** → set both `FailureCategory` and `FailureReason` inside the same lambda. Don't split into two `.Then` calls.
3. **`ActivityListener` test pattern unclear** → look at how `EmitCompensateSpan` is tested in `SagaFlowsTests.cs` (search for "Activity" in that file).

## Reference file

`src/CheckoutOrchestrator/CheckoutOrchestrator.Application/Sagas/CheckoutSaga.cs` — the canonical saga. Read it end-to-end before starting any track; the pattern is the contract.

## Wave configuration

```
REPO=/Users/chidionyema/Documents/code/haworks-platform
GH_REPO=chidionyema/haworks-platform
WAVE_MODE=modify
BASE_BRANCH=feat/checkout-saga-cheap-wins
BRIEF_FILE=docs/agent-briefs/checkout-saga-cheap-wins-spec.md
TRACK_PREFIX=feat/checkout-saga-
TRACKS=(T1 T2 T3)
WORKTREE_PARENT=/Users/chidionyema/.gemini/tmp/haworks-platform
```

## Done check (whole feature)

```
dotnet build src/CheckoutOrchestrator/CheckoutOrchestrator.Api/CheckoutOrchestrator.Api.csproj --nologo --verbosity quiet && \
dotnet test tests/CheckoutOrchestrator.Unit --nologo --filter "FullyQualifiedName~SagaFailureCategory|FullyQualifiedName~SagaDeserializeItems" && \
dotnet test tests/CheckoutOrchestrator.Integration --nologo --filter "FullyQualifiedName~PaymentExpiryWatcher"
```

All three exit 0; no warnings introduced; `bash scripts/check-architecture.sh` still passes with 0 hard violations.
