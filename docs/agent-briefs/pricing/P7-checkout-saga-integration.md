# P7 — CheckoutSaga Integration (PricingConfirmed State)

**Brief:** P7 | **Spec:** `docs/agent-briefs/pricing-tax-engine-spec.md`
**Phase:** 4 (sequential — requires P5 complete)
**Time budget:** 30 min

---

## Inputs

Read **completely** before editing:
- `src/CheckoutOrchestrator/CheckoutOrchestrator.Application/Sagas/CheckoutSaga.cs`
- `src/Contracts/Pricing/PricingRequestedEvent.cs` (from P5)
- `src/Contracts/Pricing/PriceQuoteConfirmedEvent.cs` (from P5)
- `src/Contracts/Pricing/PriceQuoteRejectedEvent.cs` (from P5)
- `src/CheckoutOrchestrator/CheckoutOrchestrator.Infrastructure/Migrations/` — existing migration snapshot

---

## Deliverable

### 1. CheckoutSagaState additions

In `CheckoutSagaState` (find it via `grep -r "class CheckoutSagaState" src/`):
```csharp
public Guid? QuoteId { get; set; }
public string? PriceQuoteJson { get; set; }
public decimal? QuotedTotal { get; set; }
public decimal? QuotedTax { get; set; }
```

Add a new EF migration for the `CheckoutSagaState` table to add these columns:
```bash
dotnet ef migrations add AddPricingToSagaState \
  --project src/CheckoutOrchestrator/CheckoutOrchestrator.Infrastructure \
  --startup-project src/CheckoutOrchestrator/CheckoutOrchestrator.Api
```

### 2. CheckoutSaga modifications

New state property:
```csharp
public State PricingConfirmedState { get; private set; } = null!;
```

New event properties:
```csharp
public Event<PriceQuoteConfirmedEvent> PriceQuoteConfirmed { get; private set; } = null!;
public Event<PriceQuoteRejectedEvent> PriceQuoteRejected { get; private set; } = null!;
```

In constructor — event correlations (add after existing events):
```csharp
Event(() => PriceQuoteConfirmed, e => e.SelectId(ctx => ctx.Message.SagaId));
Event(() => PriceQuoteRejected, e => e.SelectId(ctx => ctx.Message.SagaId));
```

**Modify `During(Initiated)` — `When(StockReserved)` transition:**
Replace `PublishAsync(PaymentSessionRequestedEvent)` with `PublishAsync(PricingRequestedEvent)`.
Do NOT start `PaymentExpirySchedule` here — move it to PricingConfirmed transition.
TransitionTo `StockReservedState` (unchanged).

**Add new handlers in `During(StockReservedState)`:**
```csharp
When(PriceQuoteConfirmed)
    .Then(ctx =>
    {
        ctx.Saga.QuoteId = ctx.Message.QuoteId;
        ctx.Saga.PriceQuoteJson = ctx.Message.PriceQuoteJson;
        ctx.Saga.TotalAmount = ctx.Message.FinalTotal;  // overwrite caller-supplied total
        ctx.Saga.QuotedTotal = ctx.Message.FinalTotal;
        ctx.Saga.QuotedTax = ctx.Message.TaxTotal;
    })
    .Schedule(PaymentExpirySchedule, ctx => ctx.Init<PaymentExpiredEvent>(new PaymentExpiredEvent
    {
        SagaId = ctx.Saga.CorrelationId,
        OrderId = ctx.Saga.OrderId,
    }))
    .PublishAsync(ctx => ctx.Init<PaymentSessionRequestedEvent>(new PaymentSessionRequestedEvent
    {
        OrderId = ctx.Saga.OrderId,
        SagaId = ctx.Saga.CorrelationId,
        Amount = ctx.Saga.TotalAmount,   // now = QuotedTotal (pricing-derived)
        Currency = ctx.Saga.Currency,
        UserId = ctx.Saga.UserId,
        CustomerEmail = ctx.Saga.CustomerEmail,
        LineItems = /* deserialize from PriceQuoteJson */,
        SuccessUrl = options.SuccessUrl,
        CancelUrl = options.CancelUrl,
        IdempotencyKey = ctx.Saga.IdempotencyKey,
    }))
    .TransitionTo(PricingConfirmedState),

When(PriceQuoteRejected)
    .Then(ctx =>
    {
        ctx.Saga.FailureReason = $"PricingFailed: {ctx.Message.Reason}";
        EmitCompensateSpan(ctx.Saga.CorrelationId, ctx.Saga.OrderId, $"pricing_failed:{ctx.Message.Reason}");
    })
    .PublishAsync(ctx => ctx.Init<StockReleaseRequestedEvent>(new StockReleaseRequestedEvent
    {
        OrderId = ctx.Saga.OrderId,
        SagaId = ctx.Saga.CorrelationId,
        Items = DeserializeItems(ctx.Saga.ReservedItemsJson),
        Reason = "pricing_failed",
    }))
    .TransitionTo(Abandoned),
```

**Rename `StockReservedState` → `PricingConfirmedState` for ReadyForPayment transitions.** The existing `During(StockReservedState)` handlers for `PaymentSessionCreated` and `PaymentSessionFailed` must be moved to `During(PricingConfirmedState)`.

The state name in the DB for existing sagas (if any) will be `"StockReserved"` — do not break backward compatibility. Name the new state `PricingConfirmedState` as a C# property but set its state string to `"PricingConfirmed"` (MT derives this from the property name automatically — `PricingConfirmedState` → `"PricingConfirmed"`).

---

## Acceptance

```bash
dotnet build src/CheckoutOrchestrator/
dotnet ef migrations list --project src/CheckoutOrchestrator/CheckoutOrchestrator.Infrastructure \
  --startup-project src/CheckoutOrchestrator/CheckoutOrchestrator.Api
# → shows AddPricingToSagaState
dotnet test tests/CheckoutOrchestrator.Integration/
# → 0 regressions on existing tests + new tests green
```

New saga tests (§12.3 of the spec):
- `Saga_transitions_through_PricingConfirmed_state_on_happy_path`
- `Saga_compensates_on_PriceQuoteRejected_and_releases_stock`
- `Saga_uses_quoted_total_not_caller_supplied_total_in_payment_request`

---

## Anti-stuck

- READ the full `CheckoutSaga.cs` before making any edit. The `DuringAny` block at the bottom has idempotency guards — do not remove them or add `PriceQuoteConfirmed`/`PriceQuoteRejected` to them (those events should never arrive late for a finalized saga).
- `PaymentExpirySchedule` starts in the `PriceQuoteConfirmed` handler, not `StockReserved`. The 15-minute window starts when pricing is confirmed.
- `ctx.Saga.TotalAmount` must be overwritten with `ctx.Message.FinalTotal` — the caller-supplied amount is no longer trusted after this brief.
- `LineItems` in `PaymentSessionRequestedEvent` must be deserialized from `PriceQuoteJson` (not from `ReservedItemsJson`) — use the `PriceQuote.Lines` to populate `PaymentLineItemData` with post-discount `FinalUnitPrice`.
- Do NOT rename the `StockReservedState` C# property — it is referenced in multiple `During()` and `When()` calls. Add `PricingConfirmedState` as an additional state.
- If any existing integration test publishes `StockReservedEvent` and expects the saga to immediately transition to `ReadyForPayment`, that test now needs to also send a `PriceQuoteConfirmedEvent`. Update those tests — they are regressions introduced by this brief and must be fixed.

---

## Done-report format

```
brief: P7
status: done | blocked
files_changed:
  - src/CheckoutOrchestrator/CheckoutOrchestrator.Application/Sagas/CheckoutSaga.cs
  - src/CheckoutOrchestrator/CheckoutOrchestrator.Infrastructure/Persistence/CheckoutSagaState.cs
  - src/CheckoutOrchestrator/CheckoutOrchestrator.Infrastructure/Migrations/YYYYMMDD_AddPricingToSagaState.cs
  - tests/CheckoutOrchestrator.Integration/CheckoutSagaTests.cs
acceptance_passed: yes | no
blockers: <none or description>
out_of_scope_observations: <optional>
```
