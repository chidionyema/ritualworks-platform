# Sagas — how they work in this codebase

The reference saga in this codebase is `CheckoutSaga`. This document explains how it works concretely so a new contributor can build a mental model without having to read every line. Today there is one saga; the patterns generalise — every future saga in the platform inherits this shape (`docs/agent-briefs/sagas-roadmap.md` § canonical checklist).

## TL;DR

A saga is a **state machine that survives in a database row**. HTTP requests, scheduled timers, and other services' events all wake it up. Between events, it costs nothing — just a row in Postgres. The state machine is defined in C# code (`CheckoutSaga.cs`); each running instance has its own row in `checkout.CheckoutSagas` keyed by `CorrelationId`. MassTransit handles event dispatch, persistence, and concurrency. Scales horizontally by adding stateless replicas of the host service.

## 1. The trigger chain — HTTP request to running saga

```
POST /api/checkouts                                              HTTP request
  │   { sagaId, orderId, userId, customerEmail, totalAmount, ... }
  ▼
CheckoutsController.Start()                                      ASP.NET Core endpoint
  │   wraps body in StartCheckoutCommand, sends via MediatR
  │   file: src/CheckoutOrchestrator/CheckoutOrchestrator.Api/Controllers/CheckoutsController.cs
  ▼
StartCheckoutCommandHandler.Handle()                             MediatR handler
  │   generates SagaId/OrderId if not supplied
  │   publishes CheckoutInitiatedEvent via IPublishEndpoint
  │   file: src/CheckoutOrchestrator/CheckoutOrchestrator.Application/Commands/StartCheckoutCommand.cs
  ▼
RabbitMQ exchange (CheckoutInitiatedEvent)                       message bus — the decoupling layer
  │
  ▼
CheckoutSaga.Initially(When(CheckoutInitiated))                  MassTransit dispatches
  │   creates new row in checkout.CheckoutSagas (sagaId = CorrelationId)
  │   transitions to Initiated state
  │   publishes StockReservationRequestedEvent
  │   file: src/CheckoutOrchestrator/CheckoutOrchestrator.Application/Sagas/CheckoutSaga.cs
  ▼
[saga sleeps until the next event arrives — just a DB row now]
```

**Crucial decoupling:** the controller never directly invokes the saga. It publishes an event. The saga subscribes. Anything that publishes `CheckoutInitiatedEvent` starts a saga instance — HTTP, scheduled jobs, another service. The controller doesn't know the saga exists.

The HTTP endpoint returns **`202 Accepted`**, not 200, because the saga is asynchronous. The client gets the sagaId back and polls `GET /api/checkouts/{sagaId}` to check progress (or subscribes to downstream events).

## 2. State machine, not a rules engine

Saga dispatch is **deterministic state-transition logic**, written in C# as fluent `Initially / During / When / TransitionTo` calls. No DSL, no reflection, no rules engine.

```csharp
During(Initiated,                                  // current state
    When(StockReserved)                            // event class
        .Then(ctx => { /* mutate saga state */ })
        .PublishAsync(ctx => ...)                  // side effect (publish another event)
        .Schedule(PaymentExpirySchedule, ...)      // arm a 15-min timer
        .TransitionTo(StockReservedState));        // → new state
```

Read every transition as the dispatch rule: **"if current state is X and event Y arrives, mutate state + emit side-effects + transition to Z."** No match → event is discarded or held depending on `OnMissingInstance` policy. The state machine class IS the dispatch table.

The CheckoutSaga's states:
- `Initial` (built-in) — pre-creation
- `Initiated` — order created, stock reservation requested
- `StockReservedState` — stock locked, payment session being created
- `ReadyForPayment` — payment session URL ready for the customer
- `Completed` (final) — happy path terminus
- `Abandoned` (final) — compensation completed
- `RequiresReview` (semi-final) — ops decides next

Compensation paths exist for every failure: `StockReservationFailedEvent`, `PaymentSessionFailedEvent`, `PaymentExpired` (timer), `PaymentAmountMismatchEvent`. Each is an explicit `When(...)` branch with its own published `StockReleaseRequestedEvent` and transition to `Abandoned`. **Every failure leaves the system in a defined state.**

## 3. State persistence — where it lives

Saga state is a Postgres table. Per service is one DB (`checkout`); the saga lives in one table within it:

```
haworks-vault-pg.internal:5432
  └── checkout                                        logical DB (per database-topology.md)
        └── checkout.CheckoutSagas                    THE saga state table
              ├── CorrelationId  uuid  PK             SagaId — the cross-service correlation key
              ├── CurrentState   text                 "Initiated", "StockReservedState", ...
              ├── Version        int                  MassTransit's optimistic-concurrency token
              ├── xmin           xid                  PostgreSQL server-side concurrency, shadow
              ├── OrderId        uuid
              ├── UserId, CustomerEmail, TotalAmount, Currency, IdempotencyKey
              ├── LineItemsJson      jsonb            snapshot of cart items
              ├── ReservedItemsJson  jsonb            populated post-StockReserved
              ├── PaymentId, PaymentSessionId, PaymentCheckoutUrl
              ├── FailureReason  text
              ├── PaymentExpiryTokenId uuid           handle for cancelling the timer
              └── CreatedAt      timestamptz
```

**One row per running saga instance.** 1000 simultaneous checkouts = 1000 rows.

Wired in DI (`src/CheckoutOrchestrator/CheckoutOrchestrator.Infrastructure/DependencyInjection.cs`):

```csharp
mt.AddSagaStateMachine<CheckoutSaga, CheckoutSagaState>()
    .EntityFrameworkRepository(r =>
    {
        r.ExistingDbContext<CheckoutDbContext>();
        r.UsePostgres();
    });
```

That single registration tells MassTransit: **whenever an event for this saga arrives, load the row by CorrelationId, run the matching state transition, save the row, commit.** All inside one EF transaction.

The `GET /api/checkouts/{sagaId}` endpoint is a thin read against this same table. Ops can query the state column directly to see where a saga is stuck: `SELECT CurrentState, COUNT(*) FROM checkout."CheckoutSagas" GROUP BY 1`.

## 4. The sleep / wake model — why long-running is "free"

Sagas don't run continuously. Each event wakes the saga, runs the matching transition (typically 10–50ms of code), commits, sleeps. Between events the saga consumes **zero CPU** — it's a database row.

```
t=0       customer POSTs /api/checkouts             saga code runs ~50ms; row inserted, StockReservationRequested published
                                                    saga is now ASLEEP (row in DB, nothing scheduled CPU-side)
t=200ms   Catalog publishes StockReserved           saga code runs ~30ms; row updated to StockReservedState
                                                    PaymentSessionRequested published
                                                    PaymentExpiry scheduled to fire at t=15min via RabbitMQ delayed exchange
                                                    saga is ASLEEP again
t=5s      Payments publishes PaymentSessionCreated  saga code runs ~20ms; row updated to ReadyForPayment
                                                    saga is ASLEEP
                                                    (customer is browsing the checkout page in their browser)
t=11min   Customer pays; Stripe webhook arrives     saga code runs ~25ms; transitions to Completed → Finalize() → row deleted
          → PaymentCompletedEvent published
```

**A saga sleeping for hours or days is identical to a saga sleeping for milliseconds.** Costs the same: one row in DB.

### How sagas wake themselves up — scheduled timers

Without an external event, a saga needs some way to fire a transition on its own — e.g., "if no payment within 15 minutes, compensate." This is what `Schedule<>` does:

```csharp
Schedule(
    () => PaymentExpirySchedule,
    instance => instance.PaymentExpiryTokenId,        // token is stored on the saga row
    s => {
        s.Delay = TimeSpan.FromMinutes(15);
        s.Received = r => r.CorrelateById(ctx => ctx.Message.SagaId);
    });

// Then on the StockReservedState transition:
.Schedule(
    PaymentExpirySchedule,
    ctx => ctx.Init<PaymentExpiredEvent>(new PaymentExpiredEvent { ... }))
```

MassTransit delegates this to **RabbitMQ's `rabbitmq_delayed_message_exchange` plugin**: the broker holds the message for 15 minutes, then delivers it. When the message arrives, the saga's `When(PaymentExpirySchedule.Received)` transition runs.

Cancellation: when the saga transitions to a terminal state in time (e.g., `PaymentCompleted`), it calls `.Unschedule(PaymentExpirySchedule)` and MassTransit cancels the pending message via the stored `PaymentExpiryTokenId`. No orphaned timeouts.

### Belt-and-braces — the fallback watcher

`src/CheckoutOrchestrator/CheckoutOrchestrator.Infrastructure/Workers/PaymentExpiryWatcher.cs` polls the saga table every 60s for rows stuck in `StockReservedState` or `ReadyForPayment` past their 15-min deadline, and publishes `PaymentExpiredEvent` directly. If the RabbitMQ scheduler plugin is missing, slow, or has dropped messages, the watcher catches it. Idempotency makes this safe: if the broker-scheduled message AND the watcher both fire, only one transition succeeds (the second arrives at an already-Abandoned saga and is ignored by the `DuringAny` guards).

**Two independent paths to fire the timeout** — the design is hedged against the message broker silently dropping scheduled messages, which has been observed in production with that plugin.

## 5. Scalability — multiple replicas

The CheckoutOrchestrator service is **stateless**. All saga state is in Postgres. You can run 1, 2, or 20 replicas and the saga behaviour is identical.

**Per-event distribution:** MassTransit uses RabbitMQ's competing-consumer pattern. Each event is delivered to ONE replica, processed there, committed. If you have 4 replicas and 100 events arrive, the load is roughly 25 events per replica. No work duplication.

**Per-saga serialisation:** the same saga instance never processes two events concurrently. If two events for the same saga arrive simultaneously on different replicas:

```
Replica A: loads sagaState row v=7; computes transition; tries UPDATE WHERE Version=7
Replica B: loads sagaState row v=7; computes transition; tries UPDATE WHERE Version=7
```

The Postgres `UPDATE` on the second commit fails because `Version` already advanced (also `xmin` shadow column catches it). MassTransit catches `DbUpdateConcurrencyException`, retries with fresh state, the second event runs against the now-Version-8 state.

**Bottleneck moves to Postgres** (single writer per saga row) and to **RabbitMQ throughput**. Both can scale further with their own clustering/sharding once you outgrow a single instance — neither is a concern at current scale.

## 6. Durability — the transactional outbox

The saga's state changes and the events it publishes are not committed independently. They share one transaction.

`AddEntityFrameworkOutbox<CheckoutDbContext>` adds two tables to the checkout DB: `OutboxMessage`, `OutboxState`. `UseBusOutbox()` wraps every saga `.PublishAsync(...)` so the to-be-published event is INSERTed into `OutboxMessage` inside the same EF transaction as the saga's `UPDATE`. The actual MQ publish happens AFTER the transaction commits — via a sweeper that reads `OutboxMessage` and ships rows to RabbitMQ.

This makes three scenarios impossible:
- "Saga advanced but the event wasn't published" — both writes are in the same transaction
- "Event was published but the saga didn't advance" — same reason
- "Event published twice on retry" — outbox de-duplicates by message-id within a configurable window (30 min in `DependencyInjection.cs`)

**The saga state and its outgoing communication are atomically consistent.** This is the single highest-value property of the outbox pattern.

If the process crashes after the transaction commits but before the outbox sweeper publishes, the next sweep picks up the row and publishes it. At-least-once delivery; consumers are required to be idempotent (and they are — see `DuringAny` guards in `CheckoutSaga.cs`).

## 7. Reliability — defence in depth

| Layer | What it does | Where |
|---|---|---|
| **MassTransit retry policy** | Transient failures (DB connection blip, MQ hiccup) → 3 immediate retries | DI defaults, can be tuned per-receive-endpoint |
| **DLQ** | Persistent failures (poison messages) → moved to error queue for ops review | MassTransit default |
| **Optimistic concurrency** | Two concurrent events on the same saga → one wins, the other retries with fresh state | `ISagaVersion` + xmin in CheckoutSagaState |
| **EF retry-on-failure** | Transient DB errors → 5 retries with backoff | `EnableRetryOnFailure(5, 500ms)` in DependencyInjection.cs |
| **Outbox** | Atomic state + publish; deduplication window | `AddEntityFrameworkOutbox` |
| **Idempotency guards** | Late-arriving duplicates on finalised sagas → silently ignored | `DuringAny(When(X).If(state != current))` |
| **Compensation paths** | Every failure mode has an explicit `When(...)` branch with reverse actions | Every `When(<FailEvent>)` in the state machine |
| **Scheduled timeouts** | Auto-recover from "external party never replies" | `Schedule<PaymentExpiredEvent>` |
| **Fallback watcher** | Catches scheduler bus failures | `PaymentExpiryWatcher` BackgroundService |
| **Telemetry on compensation** | Every compensation entry emits a `checkout.saga.compensate` span | `EmitCompensateSpan` helper |
| **Integration + chaos tests** | Compensation chain proven under failure | `SagaCompensationChaosTests.cs` |

Audit verdict from chat 2026-05-10: **exemplary saga implementation.** Best practices solidly applied; the three "cheap-win" tightening ideas (structured `FailureCategory` enum, `DeserializeItems` telemetry, `PaymentExpiryWatcher` test) are in `agent-briefs/checkout-saga-cheap-wins-spec.md`.

## 8. Failure modes — what happens when X breaks

| Failure | What happens |
|---|---|
| CheckoutOrchestrator pod crashes mid-event | Event isn't acked to RabbitMQ; redelivered to another replica or the same after restart. Saga row at the LAST committed state. No partial state. |
| RabbitMQ down | Saga consumes nothing new; existing rows in Postgres untouched. Producing services hold their outbox rows. RabbitMQ returns → queues drain. |
| Postgres down | All sagas blocked; outbox writes blocked. Returns → operation resumes. |
| `delayed-message-exchange` plugin missing | Scheduled `PaymentExpired` never fires through MT. `PaymentExpiryWatcher` polls every 60s and publishes the event directly. Worst case: ~60s late timeout. |
| Two events for the same saga arrive simultaneously | Optimistic concurrency: one commits, the other gets `DbUpdateConcurrencyException` → MT retries → reads fresh state → transitions. Strict per-saga serialisation. |
| Customer never completes payment | Saga sits in `ReadyForPayment` until t+15min; `PaymentExpired` fires; saga publishes `StockReleaseRequested`; transitions to `Abandoned`. Stock returns to inventory automatically. |
| Provider webhook re-delivers the same `PaymentCompletedEvent` twice | Saga already in `Completed`/`Finalized`; `DuringAny(When(PaymentCompleted).If(state != ReadyForPayment))` guard ignores it. No double-charge. |
| Saga stuck in an intermediate state (event never arrives) | `SELECT CurrentState, COUNT(*) FROM checkout."CheckoutSagas" WHERE CurrentState NOT IN ('Completed','Abandoned','RequiresReview') GROUP BY 1` shows the count. Ops investigates and either publishes a forcing event or extends a compensation path. |

## 9. When to use a saga vs. other patterns

| Pattern | Use it when |
|---|---|
| **Saga (state machine)** | Multi-step flow across services with explicit failure paths + need to know which step a flow is at. CheckoutSaga is canonical. |
| **Chain of event consumers** | Pipeline where each step is independent of the others' state. Simpler when there's no need to know "where am I in the flow." |
| **Synchronous orchestrator (in-process)** | The whole flow is short-lived (< 1s) and within one service. No need for durable state. |
| **Workflow engine (Temporal, Cadence)** | Long flows with complex branching, dynamic compensation, code that must execute years from now with version migration. Heavier than a state machine; right when sagas become a contortion. |

For this codebase: sagas are the right answer for cross-service flows that span more than a couple of consumers and have explicit failure paths. Refund flows, subscription renewals, onboarding journeys — these are the next saga candidates (per `agent-briefs/sagas-roadmap.md`).

## 10. File map — read these to learn

| File | Purpose |
|---|---|
| `src/CheckoutOrchestrator/CheckoutOrchestrator.Application/Sagas/CheckoutSaga.cs` | The state machine itself — the transitions are the truth |
| `src/CheckoutOrchestrator/CheckoutOrchestrator.Domain/CheckoutSagaState.cs` | The persisted state shape |
| `src/CheckoutOrchestrator/CheckoutOrchestrator.Application/Commands/StartCheckoutCommand.cs` | The MediatR command that publishes the initial event |
| `src/CheckoutOrchestrator/CheckoutOrchestrator.Api/Controllers/CheckoutsController.cs` | The HTTP entrypoint (POST /api/checkouts + GET) |
| `src/CheckoutOrchestrator/CheckoutOrchestrator.Infrastructure/DependencyInjection.cs` | MassTransit + EF saga repository + outbox wiring |
| `src/CheckoutOrchestrator/CheckoutOrchestrator.Infrastructure/CheckoutDbContext.cs` | DB schema, concurrency tokens, indexes |
| `src/CheckoutOrchestrator/CheckoutOrchestrator.Infrastructure/Workers/PaymentExpiryWatcher.cs` | The fallback timer mechanism |
| `tests/CheckoutOrchestrator/CheckoutOrchestrator.Integration/SagaCompensationChaosTests.cs` | Proves the compensation chain |
| `docs/agent-briefs/sagas-roadmap.md` | Index of candidate future sagas + the canonical checklist |
| `docs/agent-briefs/checkout-saga-cheap-wins-spec.md` | The three tightening fixes identified in the audit |
| `docs/agent-briefs/refund-saga-spec.md` | The next saga, ready to build |

Read those in roughly that order and you have the complete picture.
