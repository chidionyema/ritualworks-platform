# RefundSaga — End-to-End Spec

**Mode:** modify-existing-service. `WAVE_MODE=modify`. Lives in the existing `Payments` service (NOT a new microservice).

**Goal:** explicit lifecycle for refund flows that today exist as a chain of consumers scattered across Payments + Orders + Notifications. The saga makes ordering + compensation + observability explicit, mirroring `CheckoutSaga`'s pattern.

## 1. Why this is worth a saga

Refunds today involve at minimum:
- Operator (or webhook from Stripe/PayPal) → `RefundRequestedEvent`
- Payments calls provider API (Stripe `refund.create`, PayPal `payments.refund`)
- Provider responds (sync or via webhook)
- Orders flips the order to `Refunded` state
- Notifications emails the customer
- Audit captures the lifecycle

These flows happen as separate consumers reacting to events. **There's no single place that says "this refund is at step 3 of 5"** — operators have to grep multiple service logs to know where a refund is stuck.

A saga concentrates this. Same shape as `CheckoutSaga` for the existing checkout orchestration; the boundary is `Refund` (refunding-an-amount) not `Checkout` (committing-to-buy).

Where the saga lives: **inside the Payments service** (`src/Payments/Payments.Application/Sagas/RefundSaga.cs`). Payments owns the refund concept. Identity sees it as just another payment lifecycle.

## 2. The flow

```
Initial ─ RefundRequestedEvent ──► Requested
            │ snapshot: order_id, payment_id, amount, currency, reason,
            │           refund_id (UUID generated for cross-service ref)
            │ publish: ProviderRefundInitiationRequestedEvent
            │          (handled by StripeRefundService / PayPalRefundService)
            │ schedule: RefundTimeout (24h — provider replies usually within minutes,
            │           but webhook delays can stretch; 24h is generous)
            ▼
Requested ─ ProviderRefundInitiatedEvent ──► AwaitingProviderConfirmation
            │ saga: store provider refund id, schedule a 24h reconciliation
            ▼
AwaitingProviderConfirmation ─ ProviderRefundSucceededEvent ──► Confirmed
            │ unschedule: RefundTimeout
            │ publish: RefundCompletedEvent
            │          (Orders consumes → flip order status to Refunded)
            │          (Notifications consumes → email customer)
            │          (Audit data-mode captures the state transition via CDC)
            │ transition to terminal: Refunded
            ▼
Refunded (final)

— Compensation paths —

Requested | AwaitingProviderConfirmation ─ ProviderRefundFailedEvent ──► RequiresReview
            │ snapshot: failure reason from provider
            │ publish: RefundFailedEvent
            │          (Notifications: email customer with apology + retry path)
            │          (Audit: capture full failure for compliance trail)
            │ Terminal: RequiresReview (ops investigates; no auto-retry)

AwaitingProviderConfirmation ─ RefundTimeout fires (24h) ──► RequiresReview
            │ publish: RefundStalledEvent
            │ Ops investigates: was the provider call lost? Did the webhook fire to a dead URL?
            │ Manual resolution: ops queries provider directly, marks saga complete or escalates

Any state ─ RefundCancelledByOperatorEvent ──► Cancelled (final)
            │ ops cancels the refund attempt before it commits (rare; provider-side might already be done)
            │ if AwaitingProviderConfirmation, the saga ALSO publishes ProviderRefundCancellationRequestedEvent
              to try to cancel at the provider; outcome is best-effort
```

## 3. Saga state

```csharp
public class RefundSagaState : SagaStateMachineInstance, ISagaVersion
{
    public Guid CorrelationId { get; set; }   // SagaId = RefundId
    public string CurrentState { get; set; } = "";
    public int Version { get; set; }

    public Guid OrderId { get; set; }
    public Guid PaymentId { get; set; }
    public Guid RefundId { get; set; }        // mirrored from CorrelationId for clarity
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "USD";
    public string Reason { get; set; } = "";  // customer-cited reason, free-form
    public string Provider { get; set; } = ""; // "Stripe" | "PayPal"
    public string? ProviderRefundId { get; set; }  // populated post-ProviderRefundInitiated
    public string? FailureDetail { get; set; }
    public RefundFailureCategory FailureCategory { get; set; }
    public Guid? RefundTimeoutTokenId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public enum RefundFailureCategory
{
    None = 0,
    ProviderRefundFailed,
    RefundTimedOut,
    CancelledByOperator,
}
```

Same shape as `CheckoutSagaState`: jsonb is NOT used here (no list-of-line-items snapshot); simple scalar fields suffice.

## 4. Event contracts

Defined in `Haworks.Contracts.Payments` namespace:

| Event | Direction | Carries |
|---|---|---|
| `RefundRequestedEvent` | inbound, triggers saga | refund_id (= sagaId), order_id, payment_id, amount, currency, reason, requested_by |
| `ProviderRefundInitiationRequestedEvent` | outbound (saga → providers) | refund_id, provider, payment_id, amount, currency |
| `ProviderRefundInitiatedEvent` | inbound (provider services → saga) | refund_id, provider_refund_id |
| `ProviderRefundSucceededEvent` | inbound (Stripe/PayPal webhook handlers) | refund_id, provider_refund_id, amount_refunded, completed_at |
| `ProviderRefundFailedEvent` | inbound | refund_id, error_code, error_message |
| `ProviderRefundCancellationRequestedEvent` | outbound (best-effort cancellation) | refund_id, provider_refund_id |
| `RefundCompletedEvent` | outbound (downstream consumers) | refund_id, order_id, payment_id, amount, currency |
| `RefundFailedEvent` | outbound (downstream consumers) | refund_id, order_id, failure_category, failure_detail |
| `RefundStalledEvent` | outbound (ops notification) | refund_id, hours_since_request |
| `RefundCancelledEvent` | outbound (downstream — order state, etc.) | refund_id, order_id, reason |

The saga is the only thing publishing the outbound `Refund*Event` events. Stripe/PayPal-specific consumers translate provider webhooks into the abstract `ProviderRefund*Event`.

## 5. Downstream consumers — what reacts to the saga

| Consumer | Subscribes to | Behaviour |
|---|---|---|
| `Orders.RefundCompletedConsumer` | `RefundCompletedEvent` | flips order status to `Refunded` |
| `Orders.RefundCancelledConsumer` | `RefundCancelledEvent` | flips order status back to `Paid` (refund didn't go through) |
| `Notifications.RefundEmailConsumer` | `RefundCompletedEvent`, `RefundFailedEvent`, `RefundStalledEvent` | sends respective email templates |
| `Audit.DataModeConsumer` (post-CDC rollout) | every state transition via `EntityChangedEvent` on `refund_sagas` | full audit history |

**Compensation discipline:** the saga is the only authority on refund state. Other services REACT to saga events; they never write to refund state themselves.

## 6. Implementation tracks (parallel)

| Track | Owns | Hours |
|---|---|---|
| **T1** Saga state + DbContext + migration | `src/Payments/Payments.Domain/RefundSagaState.cs`, `src/Payments/Payments.Domain/RefundFailureCategory.cs`, `src/Payments/Payments.Infrastructure/PaymentDbContext.cs` (add `DbSet<RefundSagaState>` + mapping), `src/Payments/Payments.Infrastructure/Migrations/<ts>_AddRefundSagas.*` | 2 |
| **T2** State machine + DI registration | `src/Payments/Payments.Application/Sagas/RefundSaga.cs`, `src/Payments/Payments.Infrastructure/DependencyInjection.cs` (add `AddSagaStateMachine<RefundSaga, RefundSagaState>().EntityFrameworkRepository`) | 4 |
| **T3** Event contracts | `src/Contracts/Payments/Refund*.cs` (10 new event types per § 4) | 2 |
| **T4** Provider webhook → ProviderRefund event translators | `src/Payments/Payments.Infrastructure/Stripe/StripeRefundWebhookHandler.cs`, `src/Payments/Payments.Infrastructure/PayPal/PayPalRefundWebhookHandler.cs` (modify existing webhook entries to also publish the abstract events) | 3 |
| **T5** RefundTimeout watcher fallback | `src/Payments/Payments.Infrastructure/Workers/RefundTimeoutWatcher.cs` (mirrors `PaymentExpiryWatcher`) | 2 |
| **T6** Downstream consumers (Orders + Notifications) | `src/Orders/Orders.Application/Consumers/RefundCompletedConsumer.cs`, `RefundCancelledConsumer.cs`; `src/Notifications/Notifications.Application/Consumers/RefundEmailConsumer.cs` + 3 email templates | 3 |
| **T7** Integration tests | `tests/Payments.Integration/RefundSagaFlowsTests.cs`, `tests/Payments.Integration/RefundSagaCompensationTests.cs`, `tests/Payments.Integration/RefundTimeoutWatcherTests.cs` (mirror CheckoutOrchestrator pattern) | 4 |
| **T8** Operator API + UI hook | `src/Payments/Payments.Api/Controllers/RefundsController.cs` — POST /refunds (triggers saga via `RefundRequestedEvent`), GET /refunds/{id} (returns saga state) | 3 |

Total: ~23 hours; with 8 agents in parallel, max wall-clock ~4 hours.

## 7. The checklist (carried from `sagas-roadmap.md`)

Every box must be ticked before the feature merges:

- [ ] State machine class extends `MassTransitStateMachine<RefundSagaState>` ✓
- [ ] State class implements `SagaStateMachineInstance, ISagaVersion` ✓
- [ ] Persistence via `EntityFrameworkRepository<PaymentDbContext>` ✓
- [ ] Outbox: `AddEntityFrameworkOutbox<PaymentDbContext>` + `UseBusOutbox()` ✓
- [ ] Every inbound event correlates by SagaId (RefundId) explicitly ✓
- [ ] Every failure path explicit; no implicit drops ✓
- [ ] `RefundTimeout` scheduled in Requested transition; unscheduled on every terminal ✓
- [ ] `RefundTimeoutWatcher` background-service fallback (mirrors `PaymentExpiryWatcher`) ✓
- [ ] `DuringAny` idempotency for late duplicates ✓
- [ ] `EmitCompensateSpan` for every compensation entry ✓
- [ ] `FailureCategory` enum + `FailureDetail` string (structured + free-form) ✓
- [ ] Unit + integration + chaos tests ✓
- [ ] CDC publication includes `refund_sagas` table (for data-audit + downstream notifications) ✓
- [ ] Architecture check passes (no cross-service refs introduced) ✓

## 8. Done check

```bash
dotnet test tests/Payments.Integration --nologo -c Release \
  --filter "FullyQualifiedName~RefundSaga"
bash scripts/check-architecture.sh
```

Both exit 0. New saga handles the full happy-path + compensation chaos test + timeout test green.

## 9. Wave configuration

```
REPO=/Users/chidionyema/Documents/code/haworks-platform
GH_REPO=chidionyema/haworks-platform
WAVE_MODE=modify
BASE_BRANCH=feat/refund-saga
BRIEF_FILE=docs/agent-briefs/refund-saga-spec.md
TRACK_PREFIX=feat/refund-saga-
TRACKS=(T1 T2 T3 T4 T5 T6 T7 T8)
WORKTREE_PARENT=/Users/chidionyema/.gemini/tmp/haworks-platform
```

## 10. Reference files

- `src/CheckoutOrchestrator/CheckoutOrchestrator.Application/Sagas/CheckoutSaga.cs` — the canonical pattern; read end-to-end before writing this saga.
- `src/CheckoutOrchestrator/CheckoutOrchestrator.Domain/CheckoutSagaState.cs` — state class shape.
- `src/CheckoutOrchestrator/CheckoutOrchestrator.Infrastructure/DependencyInjection.cs` — DI + outbox + saga repository registration.
- `src/CheckoutOrchestrator/CheckoutOrchestrator.Infrastructure/Workers/PaymentExpiryWatcher.cs` — the fallback-watcher pattern that `RefundTimeoutWatcher` mirrors.
- `tests/CheckoutOrchestrator.Integration/SagaCompensationChaosTests.cs` — the integration-test pattern; T7's tests mirror this fixture.
- `docs/agent-briefs/sagas-roadmap.md` — the master sequence + checklist.
