# Sagas — additional candidates roadmap

You have one saga (`CheckoutSaga` — exemplary, audit on 2026-05-10). Four candidates were identified for additional sagas. This is the index.

| Saga | Spans | Prereqs | Readiness | Spec |
|---|---|---|---|---|
| **RefundSaga** | Payments + Orders + Notifications + Audit | None — all services exist | ✅ ready | [`refund-saga-spec.md`](./refund-saga-spec.md) |
| **SubscriptionSaga** | Payments + Notifications + Audit + (Billing if added) | Payments subscription migrations exist (Payments.Infrastructure already has `AddSubscriptionAndWebhookEvents` migration) but renewal scheduling needs scheduler-svc + dunning policy needs product input | ⚠️ specs blocked on product decisions | not drafted |
| **OnboardingSaga** | Identity + Notifications + (Content for avatar?) + analytics if any | Product owns the onboarding flow definition — what steps, what timeouts, what compensation for incomplete signups | ⚠️ specs blocked on product input | not drafted |
| **FulfillmentSaga** | Inventory + Shipping + Orders + Notifications + Audit | Inventory-svc + Shipping-svc don't exist yet (Tier 2 in `cross-cutting-services-roadmap.md`) | ⛔ blocked on Tier 2 services | not drafted |

## Sequencing recommendation

1. **RefundSaga first** — it's fully buildable today and addresses a real ops gap (refund flows likely a chain of consumers today; saga makes lifecycle + failure handling explicit).
2. **SubscriptionSaga second**, after product clarifies: dunning retry cadence (3 attempts? 5?), grace-period length, cancellation policy (immediate or end-of-period). Without those decisions the saga can't be specced concretely.
3. **OnboardingSaga third**, after product defines the onboarding flow (steps, mandatory vs optional, abandonment policy).
4. **FulfillmentSaga last** — gated on the Tier 2 build of inventory-svc + shipping-svc (per `cross-cutting-services-roadmap.md`).

## Pattern to mirror across all of these

`CheckoutSaga` is the canonical reference. Every new saga inherits its structure verbatim:

| Element | What |
|---|---|
| State machine class | `MassTransitStateMachine<<Name>SagaState>` |
| State class | Implements `SagaStateMachineInstance, ISagaVersion`; jsonb columns for snapshot data; `Version` + xmin both as concurrency tokens |
| Persistence | EF saga repository via `AddSagaStateMachine<X, XState>().EntityFrameworkRepository(...)` |
| Outbox | `AddEntityFrameworkOutbox<XDbContext>` + `UseBusOutbox()` so state + publish commit atomically |
| Correlation | `Event(() => X, e => e.SelectId(ctx => ctx.Message.SagaId))` on every event; explicit `CorrelateBy + OnMissingInstance.Discard()` for events without SagaId |
| Compensation | every non-terminal state has explicit `When(<Failure>)` → publish reverse event → transition to terminal |
| Timeouts | `Schedule<X>` with token stored on saga state; `Unschedule` on every terminal transition |
| Fallback | `BackgroundService` polling for stuck sagas if scheduler is unreliable (see `PaymentExpiryWatcher`) |
| Idempotency | `DuringAny(When(X).If(...))` guards against late duplicates on finalized states |
| Telemetry | `<feature>.saga.compensate` activity emitted on every compensation entry; OTLP propagates correlation ID |
| Failure tracking | structured `FailureCategory` enum (post the `checkout-saga-cheap-wins-spec.md` lands) + `FailureReason` string for human-readable detail |
| Tests | unit-test state machine in isolation; integration test with Testcontainers; chaos test for compensation path |

Every new saga spec MUST tick this checklist. Deviations get justified in the spec body.

## What the broader codebase gains per saga

Each new saga moves business state currently scattered across consumer-chains into one orchestrator. Improvements that compound:
- **Visibility**: the saga's state column tells you exactly where a flow is stuck
- **Audit story**: data-mode CDC captures every state transition → ops can answer "what was the state at 2pm?"
- **Failure handling**: explicit `Abandoned` / `RequiresReview` terminal states give ops a queue to drain
- **Operability**: `EmitCompensateSpan` per saga gives a single OTLP query for "all compensations this hour"
- **Testability**: state machine is unit-testable without spinning up the full bus

## Open questions per saga (for the deferred ones)

### SubscriptionSaga questions for product
- Retry cadence for failed renewals (number of attempts + intervals)
- Grace-period length (3 days, 7 days, 14 days?)
- Cancellation policy: immediate revoke vs end-of-period
- Trial-period handling (separate state? embedded?)
- Plan change mid-cycle: how is proration handled?
- Pause vs cancel: separate saga or shared?

### OnboardingSaga questions for product
- Required vs optional steps in onboarding
- Email verification timeout — what happens if user never verifies? (cancel? grace period? remind?)
- Mobile vs web parity — same saga or different?
- Welcome flow side effects: notifications, sample data, default subscriptions, …?
- Abandonment metrics: when does an abandoned signup get cleaned up?

### FulfillmentSaga questions (waiting on Tier 2 services)
- Inventory reservation TTL — same 15min as checkout? longer (orders are paid)?
- Multi-warehouse fulfillment: split order across warehouses?
- Carrier failure compensation: re-route? cancel? alert?
- Customer-pickup vs ship: same saga or different?

## Cross-reference

- Each saga that ships needs CDC integration (every state transition becomes an `EntityChangedEvent` → data-mode audit captures the full lifecycle). See `cdc-service-spec.md` § 6.3 (data-mode audit consumer).
- Each saga's tables should be in the CDC publication (`infra/stateful/cdc-publications/<service>.sql`) so downstream consumers (search, analytics, webhooks) can react to saga state changes if they need.
