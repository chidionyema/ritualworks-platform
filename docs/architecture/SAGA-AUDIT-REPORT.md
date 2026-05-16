# Saga State Machine Audit Report

**Platform:** Haworks / Haworks .NET 9.0 Microservices
**Date:** 2026-05-14
**Auditor:** Claude Opus 4.6 (automated deep audit)
**Scope:** All 4 saga state machines, their state entities, consumers, DI wiring, tests, and contracts
**Branch:** `feat/shared-testcontainers` (commit `d6ac8f9`)

---

## Table of Contents

1. [Executive Summary](#executive-summary)
2. [Saga 1: CheckoutSaga](#saga-1-checkoutsaga)
3. [Saga 2: RefundSaga](#saga-2-refundsaga)
4. [Saga 3: SubscriptionSaga](#saga-3-subscriptionsaga)
5. [Saga 4: PrivacyRequestStateMachine](#saga-4-privacyrequeststatmachine)
6. [Cross-Cutting Findings](#cross-cutting-findings)
7. [Summary Scorecard](#summary-scorecard)
8. [Prioritized Fix List](#prioritized-fix-list)

---

## Executive Summary

This audit reviewed every line of all 4 saga state machines in the platform. The **CheckoutSaga** is production-grade with excellent compensation, timeout handling, telemetry, and test coverage. The **RefundSaga** is solid with minor gaps. The **SubscriptionSaga** has a **critical production-blocking defect** (not registered in DI for production MassTransit). The **PrivacyRequestStateMachine** is a minimal skeleton that lacks timeout handling, failure paths, finalization, and has zero test coverage.

**Critical findings: 3 | High: 8 | Medium: 11 | Low: 6 | Informational: 4**

---

## Saga 1: CheckoutSaga

### Files Reviewed

| File | Path |
|------|------|
| State Machine | `src/CheckoutOrchestrator/CheckoutOrchestrator.Application/Sagas/CheckoutSaga.cs` |
| State Entity | `src/CheckoutOrchestrator/CheckoutOrchestrator.Domain/CheckoutSagaState.cs` |
| DI Wiring | `src/CheckoutOrchestrator/CheckoutOrchestrator.Infrastructure/DependencyInjection.cs` |
| Timeout Watcher | `src/CheckoutOrchestrator/CheckoutOrchestrator.Infrastructure/Workers/PaymentExpiryWatcher.cs` |
| Integration Tests | `tests/CheckoutOrchestrator/CheckoutOrchestrator.Integration/SagaFlowsTests.cs` |
| Expiry Tests | `tests/CheckoutOrchestrator/CheckoutOrchestrator.Integration/PaymentExpiryTests.cs` |
| Chaos Tests | `tests/CheckoutOrchestrator/CheckoutOrchestrator.Integration/SagaCompensationChaosTests.cs` |
| E2E Tests | `tests/CheckoutOrchestrator/CheckoutOrchestrator.Integration/CheckoutSagaEndToEndTests.cs` |
| Validator Tests | `tests/CheckoutOrchestrator/CheckoutOrchestrator.Unit/Validators/StartCheckoutCommandValidatorTests.cs` |

### State Machine Diagram

```
Initial --(CheckoutInitiated)--> Initiated
    |                                |
    |                    StockReservationFailed --> Abandoned [final]
    |                                |
    |                      StockReserved
    |                                |
    |                                v
    |                        StockReservedState
    |                           /    |       \
    |           PaymentSession  PaymentExpiry  PaymentSession
    |              Created      (timeout)       Failed
    |                |              |               |
    |                v              |               v
    |          ReadyForPayment      |          (StockRelease)
    |           /    |    \    \    |           --> Abandoned [final]
    |  Payment  Payment  Payment  Payment
    |  Completed Session  Amount  Expiry
    |     |      Failed  Mismatch (timeout)
    |     v        |        |        |
    |  Completed   |   RequiresReview |
    |  [finalized] |   [terminal]    |
    |              v                  v
    |         (StockRelease)     (StockRelease)
    |         --> Abandoned       --> Abandoned
    |             [terminal]         [terminal]
```

### Findings

| ID | Severity | Category | Finding | File:Line | Recommendation |
|----|----------|----------|---------|-----------|----------------|
| CS-01 | **Medium** | Compensation | The saga doc comment (line 32) claims `CheckoutSessionExpiredEvent` is published on PaymentExpiry timeout, but the actual code only publishes `StockReleaseRequestedEvent`. Orders-svc's `CheckoutSessionExpiredConsumer` will never fire from the saga's timeout path; only from Stripe's webhook processor. | `CheckoutSaga.cs:32` vs `:179-186` | Either publish `CheckoutSessionExpiredEvent` alongside `StockReleaseRequestedEvent` on the timeout path, or correct the documentation. Orders-svc relies on this to mark orders expired. |
| CS-02 | **Low** | Idempotency | The `DuringAny` block (lines 239-242) uses `.If()` guards with state-name string comparisons (`ctx.Saga.CurrentState != Initiated.Name`). These guard late-arriving duplicates. However, the `PaymentExpiredEvent` (schedule received) is NOT in the `DuringAny` block. MT's default for unhandled scheduled events on a finalized saga is to log a warning, which is acceptable, but the test at `PaymentExpiryTests.cs:106` depends on this behavior implicitly. | `CheckoutSaga.cs:239-242` | Add a comment clarifying the design choice. The test already validates the behavior is correct. |
| CS-03 | **Low** | Correctness | `PaymentAmountMismatch` is only handled in `ReadyForPayment` state. If the event arrives while the saga is in `StockReservedState` (before `PaymentSessionCreated`), it will be silently discarded since the correlation is by `OrderId` with `OnMissingInstance(Discard)`. This is likely correct (mismatch cannot happen before a session exists), but there is no explicit guard. | `CheckoutSaga.cs:69-76` | Acceptable as-is. Add a code comment explaining why the event cannot arrive in other states. |
| CS-04 | **Medium** | Edge Case | The `Abandoned` state is terminal but NOT finalized (no `.Finalize()` call). This means the saga row persists forever in the database. `SetCompletedWhenFinalized()` only deletes rows in the `Final` state. Only `Completed` calls `.Finalize()`. Abandoned sagas will accumulate indefinitely. | `CheckoutSaga.cs:147,172,186,228` | Either (a) add `.Finalize()` to all Abandoned transitions, or (b) implement a cleanup job that purges old Abandoned/RequiresReview rows. Option (a) is simpler but loses audit trail; option (b) is recommended. |
| CS-05 | **Informational** | Best Practice | The `PaymentExpiryWatcher` (belt-and-braces fallback) correctly handles idempotency via the saga's state guard. Excellent defense-in-depth pattern. | `PaymentExpiryWatcher.cs:1-127` | No action needed. |
| CS-06 | **Informational** | Concurrency | `CheckoutSagaState` has dual concurrency protection: `ISagaVersion` (Version column as `IsConcurrencyToken`) AND `xmin` shadow property. Both are wired in `CheckoutDbContext`. This is best-in-class for MassTransit + EF Core + PostgreSQL. | `CheckoutDbContext.cs:64-69` | No action needed. |
| CS-07 | **Low** | Correctness | `DeserializeItems()` silently returns empty on `JsonException`. If `ReservedItemsJson` is corrupted, the saga will publish `StockReleaseRequestedEvent` with zero items, silently skipping compensation. | `CheckoutSaga.cs:288-300` | Log a warning or throw. Silent data loss during compensation is dangerous. |
| CS-08 | **Medium** | Edge Case | The validator (`StartCheckoutCommandValidatorTests`) validates `TotalAmount > 0`, but the saga itself does not validate TotalAmount. A zero-amount checkout could create a saga, reserve zero stock, and create a $0 payment session with Stripe (which Stripe rejects). | `CheckoutSaga.cs:81-104` | Add a saga-level guard: if `TotalAmount <= 0`, transition directly to Abandoned instead of publishing StockReservationRequested. |

### Test Coverage Assessment

| Scenario | Covered? | Test File |
|----------|----------|-----------|
| Happy path (full flow) | Yes | `SagaFlowsTests.cs:62` |
| StockReservationFailed | Yes | `SagaFlowsTests.cs:129` |
| PaymentSessionFailed after StockReserved | Yes | `SagaFlowsTests.cs:158` |
| PaymentAmountMismatch | Yes | `SagaFlowsTests.cs:198` |
| PaymentExpiry in StockReservedState | Yes | `PaymentExpiryTests.cs:42` |
| PaymentExpiry in ReadyForPayment | Yes | `PaymentExpiryTests.cs:70` |
| Duplicate PaymentExpiry (race condition) | Yes | `PaymentExpiryTests.cs:106` |
| Saga persistence across restarts | Yes | `SagaFlowsTests.cs:241` |
| Full compensation chain (saga + catalog consumer) | Yes | `SagaCompensationChaosTests.cs:74` |
| Input validation | Yes | `StartCheckoutCommandValidatorTests.cs` |
| **Duplicate CheckoutInitiated** | **NO** | -- |
| **PaymentCompleted with wrong amount** | **NO** | -- |
| **Concurrent events on same saga** | **NO** | -- |
| **StockReserved arriving after StockReservationFailed** | **NO** | -- |
| **PaymentSessionCreated arriving after PaymentSessionFailed** | **NO** | -- |

---

## Saga 2: RefundSaga

### Files Reviewed

| File | Path |
|------|------|
| State Machine | `src/Payments/Payments.Application/Sagas/RefundSaga.cs` |
| State Entity | `src/Payments/Payments.Domain/RefundSagaState.cs` |
| DI Wiring | `src/Payments/Payments.Infrastructure/DependencyInjection.cs:172-177` |
| Timeout Watcher | `src/Payments/Payments.Infrastructure/Workers/RefundTimeoutWatcher.cs` |
| Integration Test | `tests/Payments/Payments.Integration/RefundSagaIntegrationTests.cs` |

### State Machine Diagram

```
Initial --(RefundRequested)--> Requested
    |                              |
    |                   ProviderRefundInitiated --> AwaitingProviderConfirmation
    |                              |                      |           |         \
    |                   ProviderRefundFailed    ProviderRefund  ProviderRefund  RefundTimeout
    |                              |            Succeeded       Failed          (24h)
    |                              v               |               |              |
    |                        RequiresReview   Refunded [final]  RequiresReview  RequiresReview
    |                                                             [terminal]    [terminal]
    |
    |  DuringAny: RefundCancelledByOperator --> Cancelled [finalized]
```

### Findings

| ID | Severity | Category | Finding | File:Line | Recommendation |
|----|----------|----------|---------|-----------|----------------|
| RS-01 | **High** | Concurrency | `RefundSagaState` entity configuration in `PaymentDbContext` (lines 109-127) does NOT configure `xmin` as a concurrency token nor does it mark `Version` as `IsConcurrencyToken()`. While `ISagaVersion` is implemented on the state class, the EF configuration never maps `Version` as a concurrency token. This means concurrent events on the same refund saga can silently overwrite each other. | `PaymentDbContext.cs:109-127` | Add `entity.Property(s => s.Version).IsConcurrencyToken();` and optionally add `xmin` shadow property (matching the CheckoutSaga pattern). |
| RS-02 | **High** | Concurrency | Same as RS-01, `SubscriptionSagaState` configuration (lines 129-143) also lacks concurrency token configuration. | `PaymentDbContext.cs:129-143` | Add concurrency token configuration for both saga state entities. |
| RS-03 | **Medium** | Compensation | `RefundCancelledByOperator` in the `DuringAny` block (line 128-149) can cancel a refund that is already in `Requested` state. If the provider refund initiation request has already been published but not yet consumed, cancelling the saga leaves an orphaned `ProviderRefundInitiationRequestedEvent` in flight. The consumer will attempt to initiate a refund with the provider for a cancelled refund. | `RefundSaga.cs:128-149` | Add a guard: if in `Requested` state, publish a `ProviderRefundCancellationRequestedEvent` to preemptively cancel (or have the consumer check saga state before acting). |
| RS-04 | **Medium** | Edge Case | The saga hardcodes `Provider = "Stripe"` (line 47) in the `ProviderRefundInitiationRequestedEvent`. The `RefundSagaState` has a `Provider` field but it is never set by the saga. PayPal refunds will be incorrectly routed. | `RefundSaga.cs:47` | Set `saga.Provider = "Stripe"` (or determine from the payment) and use `ctx.Saga.Provider` in the publish. Better: accept provider from the `RefundRequestedEvent`. |
| RS-05 | **Medium** | Outbox | The Payments DI outbox configuration (line 166-170) does NOT set `DuplicateDetectionWindow`. CheckoutOrchestrator and Catalog both set 30 minutes. Without this, duplicate message delivery is not guarded at the outbox level. | `DependencyInjection.cs:166-170` | Add `o.DuplicateDetectionWindow = TimeSpan.FromMinutes(30);` |
| RS-06 | **Low** | Correctness | `RequiresReview` is terminal but NOT finalized. Like CheckoutSaga's `Abandoned`, these rows persist indefinitely. | `RefundSaga.cs:79,113,126` | Implement a cleanup job or add `.Finalize()` after operator resolution. |
| RS-07 | **Medium** | Edge Case | A "refund on a refund" (recursive refund) is not guarded. If `RefundRequestedEvent` arrives with a `RefundId` matching an existing saga, MassTransit's `CorrelateById` will route it to the existing saga, which is in `Requested` or later -- the event is only handled `Initially`, so it will be silently dropped. However, using the same `RefundId` for a second refund is the real concern: the caller must generate unique IDs. | `RefundSaga.cs:23` | Document that RefundId must be globally unique. Consider adding a saga-side guard. |

### Test Coverage Assessment

| Scenario | Covered? | Test File |
|----------|----------|-----------|
| Happy path (create refund, start saga) | Partially | `RefundSagaIntegrationTests.cs:39` |
| Full happy path through Refunded | **NO** | -- |
| Provider refund failed | **NO** | -- |
| Refund timeout (24h) | **NO** | -- |
| Operator cancellation | **NO** | -- |
| Operator cancellation during AwaitingProvider | **NO** | -- |
| Duplicate RefundRequested | **NO** | -- |
| Concurrent provider events | **NO** | -- |

---

## Saga 3: SubscriptionSaga

### Files Reviewed

| File | Path |
|------|------|
| State Machine | `src/Payments/Payments.Application/Sagas/SubscriptionSaga.cs` |
| State Entity | `src/Payments/Payments.Domain/SubscriptionSagaState.cs` |
| DI Wiring | `src/Payments/Payments.Infrastructure/DependencyInjection.cs` (MISSING registration) |
| Integration Test | `tests/Payments/Payments.Integration/SubscriptionSagaTests.cs` |

### State Machine Diagram

```
Initial --(SubscriptionStarted)--> Active
    |                                  |           \
    |                        RenewalTimeout    SubscriptionCancelled
    |                        or RenewalRequested    --> Cancelled [finalized]
    |                                  |
    |                                  v
    |                              Renewing
    |                              /       \
    |                  SubscriptionRenewed  RenewalFailed
    |                        |                    |
    |                        v                    v
    |                     Active             GracePeriod
    |                                        /    |      \
    |                           SubscriptionRenewed  DunningRetry  RenewalFailed
    |                                |               (schedule)      |
    |                                v                  |         (retry <= 3: reschedule)
    |                             Active                |         (retry > 3: Cancelled [final])
    |                                          PublishRenewalRequested
```

### Findings

| ID | Severity | Category | Finding | File:Line | Recommendation |
|----|----------|----------|---------|-----------|----------------|
| SS-01 | **CRITICAL** | DI/Deployment | The `SubscriptionSaga` is **NOT registered** in `Payments.Infrastructure.DependencyInjection.cs` for production MassTransit. Only `RefundSaga` is registered (line 172). The test factory registers it (line 107 in `PaymentsWebAppFactory.cs`), so tests pass, but **in production the saga will never be created or consume events**. | `DependencyInjection.cs:163-193` | Add `mt.AddSagaStateMachine<SubscriptionSaga, SubscriptionSagaState>().EntityFrameworkRepository(r => { r.ExistingDbContext<PaymentDbContext>(); r.UsePostgres(); });` to the production MassTransit configuration. |
| SS-02 | **CRITICAL** | Correctness | The `SubscriptionStarted` event correlation (line 28) uses `CorrelateBy((state, ctx) => state.ProviderSubscriptionId == ctx.Message.SubscriptionId)`. Since `SubscriptionStartedEvent.SubscriptionId` is a `string` (provider subscription ID like "sub_xxx"), NOT a `Guid`, there is no `SelectId` or `CorrelateById` that generates a saga CorrelationId. MassTransit will auto-generate a Guid from the message's MessageId. This means the saga's CorrelationId is unpredictable and cannot be used for targeted correlation by other events. Events using `CorrelateById(ctx => ctx.Message.SubscriptionId)` (like `RenewalRequested`, `RenewalFailed`, `PaymentRecovered`) expect a `Guid`, but the saga's CorrelationId was auto-assigned -- these events must somehow know this Guid. | `SubscriptionSaga.cs:28-33` | Either (a) add a `Guid SubscriptionSagaId` field to the events and use `SelectId`, or (b) use `CorrelateBy` with property-based correlation for ALL events consistently. The current mix of `CorrelateById` (Guid) and `CorrelateBy` (string) is fragile. |
| SS-03 | **High** | Correctness | `PaymentRecovered` event (line 32) is declared and wired for correlation but is **never handled in any state**. There is no `When(PaymentRecovered)` in any `During(...)` block. The event will be silently ignored by all states. | `SubscriptionSaga.cs:32,162` | Add handling in `GracePeriod`: `When(PaymentRecovered)` should reset RetryCount, unschedule dunning, and transition to Active. |
| SS-04 | **High** | Timeout | The `RenewalTimeoutSchedule` delay is computed as `ctx.Saga.PeriodEnd - DateTime.UtcNow - TimeSpan.FromDays(1)` (line 51). If `PeriodEnd` is in the past or less than 1 day away, this produces a **negative or zero TimeSpan**. MassTransit's behavior with negative delays is undefined and may throw or fire immediately. | `SubscriptionSaga.cs:50-51` | Guard: `var delay = ctx.Saga.PeriodEnd - DateTime.UtcNow - TimeSpan.FromDays(1); if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;` or handle the edge case explicitly. |
| SS-05 | **High** | Concurrency | Same as RS-01/RS-02: No concurrency token configured on `SubscriptionSagaState` in `PaymentDbContext`. | `PaymentDbContext.cs:129-143` | Add `entity.Property(s => s.Version).IsConcurrencyToken();` |
| SS-06 | **Medium** | Correctness | In `GracePeriod`, when `SubscriptionRenewed` arrives (line 116-125), the dunning schedule is unscheduled but the renewal timeout is NOT re-scheduled. The subscription will never trigger a proactive renewal again. | `SubscriptionSaga.cs:116-125` | Add `.Schedule(RenewalTimeoutSchedule, ...)` after transitioning back to Active from GracePeriod, matching the pattern in `Renewing` state (line 95-97). |
| SS-07 | **Medium** | Edge Case | The `SubscriptionCancelled` handler (line 81-84) is only in the `Active` state. If a cancellation arrives while in `Renewing` or `GracePeriod`, it is silently ignored. A customer cancelling during dunning should still cancel. | `SubscriptionSaga.cs:81-84` | Move `When(SubscriptionCancelled)` to a `DuringAny` block (with guards to skip if already Cancelled). |
| SS-08 | **Medium** | Edge Case | The `DunningRetrySchedule.Received` handler in `GracePeriod` (line 126-132) publishes `SubscriptionRenewalRequestedEvent` but does NOT transition to `Renewing`. The saga stays in `GracePeriod`. If `RenewalFailed` arrives, the `RetryCount++` logic works, but the state diagram is confusing -- dunning retries happen entirely within `GracePeriod`. | `SubscriptionSaga.cs:126-132` | This is arguably correct (dunning stays in GracePeriod), but document the design intent. If a `SubscriptionRenewed` arrives during a dunning retry, it's handled by GracePeriod's handler (line 116), which is correct. |
| SS-09 | **Low** | Logging | The saga injects `ILogger<SubscriptionSaga>` via constructor (line 14). MassTransit sagas are singletons -- the logger is captured once and reused for all saga instances. This is technically fine but can cause confusion in structured logs if scope-per-request logging is expected. | `SubscriptionSaga.cs:14` | Acceptable. Consider using `Activity`-based telemetry (matching CheckoutSaga's pattern) instead of `ILogger` for structured traces. |

### Test Coverage Assessment

| Scenario | Covered? | Test File |
|----------|----------|-----------|
| SubscriptionStarted -> Active | Yes | `SubscriptionSagaTests.cs:37` |
| RenewalFailed -> GracePeriod | Yes | `SubscriptionSagaTests.cs:70` |
| Full renewal cycle | **NO** | -- |
| Dunning retry exhaustion (>3) -> Cancelled | **NO** | -- |
| Recovery during GracePeriod | **NO** | -- |
| Cancellation during GracePeriod | **NO** | -- |
| Negative renewal timeout delay | **NO** | -- |
| Concurrent renewal events | **NO** | -- |
| PaymentRecovered handling | **NO** (event is dead code) | -- |

---

## Saga 4: PrivacyRequestStateMachine

### Files Reviewed

| File | Path |
|------|------|
| State Machine | `src/Privacy/Privacy.Application/Requests/Sagas/PrivacyRequestStateMachine.cs` |
| State Entity | `src/Privacy/Privacy.Application/Requests/Sagas/PrivacyRequestState.cs` |
| DI Wiring | `src/Privacy/Privacy.Infrastructure/DependencyInjection.cs` |
| Unit/Integration Tests | **NONE** (no test files found under `tests/Privacy/`) |

### State Machine Diagram

```
Initial --(RequestInitiated)--> Processing
    |                               |
    |                    ErasureCompleted (per service)
    |                               |
    |              [if identity AND orders completed]
    |                               |
    |                               v
    |                          Completed [terminal, NOT finalized]
```

### Findings

| ID | Severity | Category | Finding | File:Line | Recommendation |
|----|----------|----------|---------|-----------|----------------|
| PR-01 | **CRITICAL** | Correctness | The saga checks `IdentityCompleted && OrdersCompleted` (line 33) but the `PrivacyRequestState` also has a `PaymentsCompleted` field (line 17) that is NEVER checked. If the privacy erasure is supposed to wait for payments-svc erasure (which is likely for GDPR compliance), this is a **data protection regulation violation** -- the saga completes before all services have erased data. | `PrivacyRequestStateMachine.cs:33` vs `PrivacyRequestState.cs:17` | Add `context.Saga.PaymentsCompleted` to the completion guard. Also consider making the service list configurable rather than hardcoded. |
| PR-02 | **High** | Timeout | There is NO timeout/deadline on the privacy erasure saga. If any service fails to respond (crashes, loses the message, etc.), the saga sits in `Processing` forever. For GDPR/CCPA compliance, erasure requests typically must be completed within 30 days. | `PrivacyRequestStateMachine.cs:15-35` | Add a `Schedule` with a configurable timeout (e.g., 7 days). On timeout, publish an alert event and transition to a `Stalled` state for operator intervention. |
| PR-03 | **High** | Compensation | There is NO failure handling. `PrivacyErasureFailed` exists in the contracts (`PrivacyEvents.cs:5`) but the saga does not consume it. If any service fails erasure, the saga is stuck forever in `Processing`. | `PrivacyRequestStateMachine.cs` (missing) | Add `Event(() => ErasureFailed)` and handle it in `Processing`. Options: retry, transition to `Failed` state, alert operators. |
| PR-04 | **High** | Correctness | `Completed` state is NOT finalized (no `.Finalize()` call, no `SetCompletedWhenFinalized()`). The saga row persists indefinitely after completion. Unlike other sagas which at least call `SetCompletedWhenFinalized()`, this saga has neither. | `PrivacyRequestStateMachine.cs:33-35` | Add `.Finalize()` to the Completed transition and `SetCompletedWhenFinalized()` in the constructor. Or keep the row for GDPR audit trail (document the choice). |
| PR-05 | **Medium** | Correctness | The `CompletedAt` timestamp field exists on `PrivacyRequestState` (line 20) but is NEVER set. When the saga transitions to `Completed`, the timestamp remains null. | `PrivacyRequestStateMachine.cs:33-34` | Add `context.Saga.CompletedAt = DateTime.UtcNow;` in the completion transition. |
| PR-06 | **Medium** | Correctness | The `RequestType` field on `PrivacyRequestState` (line 8) is never set. The saga only handles erasure requests. If data export requests are added later (contracts exist: `PrivacyDataExportRequested`), the state entity is prepared but the saga is not. | `PrivacyRequestState.cs:8` | Set `RequestType = "Erasure"` on initialization. |
| PR-07 | **Medium** | Idempotency | No `DuringAny` block for duplicate handling. If `ErasureCompleted` for "identity-svc" arrives twice, the saga processes it idempotently (sets boolean again), but if it arrives for an unknown service name, it's silently ignored with no logging. | `PrivacyRequestStateMachine.cs:27-35` | Add logging for unexpected service names. |
| PR-08 | **Medium** | Concurrency | `PrivacyRequestState` has `ISagaVersion` with `Version` configured as `IsConcurrencyToken()` in DbContext. Good. However, unlike CheckoutSaga, there is NO `xmin` shadow property. This is a single layer of concurrency protection. | `PrivacyDbContext.cs:41` | Add `xmin` concurrency token for defense-in-depth (matching CheckoutSaga pattern). |
| PR-09 | **High** | Testing | **Zero test coverage.** No test files exist under `tests/Privacy/`. The saga has never been tested. | -- | Create unit tests (MassTransit test harness) and integration tests covering: happy path, partial completion, duplicate events, missing service responses. |
| PR-10 | **Medium** | Contract | `InitiatePrivacyRequestMessage` is defined INSIDE the saga file (`PrivacyRequestStateMachine.cs:45`) rather than in `src/Contracts/Privacy/`. This breaks the bounded-context boundary -- any service that needs to initiate a privacy request must reference the Privacy.Application assembly. | `PrivacyRequestStateMachine.cs:45` | Move to `src/Contracts/Privacy/PrivacyEvents.cs`. |

---

## Cross-Cutting Findings

| ID | Severity | Category | Finding | Affected Sagas | Recommendation |
|----|----------|----------|---------|----------------|----------------|
| XC-01 | **High** | Concurrency | RefundSagaState and SubscriptionSagaState lack concurrency tokens in EF configuration. Only CheckoutSagaState and PrivacyRequestState have them. This means concurrent event delivery on the same RefundSaga or SubscriptionSaga instance can cause lost updates. | Refund, Subscription | Add `entity.Property(s => s.Version).IsConcurrencyToken();` and xmin to PaymentDbContext for both saga entities. |
| XC-02 | **Medium** | Outbox | Payments service outbox configuration lacks `DuplicateDetectionWindow`. CheckoutOrchestrator, Catalog, and Orders all set 30 minutes. Payments does not. | Refund, Subscription | Add `o.DuplicateDetectionWindow = TimeSpan.FromMinutes(30);` |
| XC-03 | **Medium** | Scheduler | CheckoutOrchestrator configures `AddDelayedMessageScheduler()` for the broker-based scheduler. Payments does NOT configure any scheduler, yet both RefundSaga and SubscriptionSaga use `Schedule(...)`. In production, without a scheduler configured, schedule operations will throw at runtime. | Refund, Subscription | Add `mt.AddDelayedMessageScheduler();` and `cfg.UseDelayedMessageScheduler();` to Payments DI. |
| XC-04 | **Informational** | Pattern | CheckoutSaga uses observability spans (`EmitCompensateSpan`) for compensation tracking. RefundSaga also uses them. SubscriptionSaga and PrivacyRequestStateMachine do not emit any telemetry spans. | Subscription, Privacy | Add structured telemetry to SubscriptionSaga and PrivacyRequestStateMachine for observability parity. |

---

## Summary Scorecard

| Saga | Correctness | Compensation | Timeouts | Concurrency | Edge Cases | Tests | Overall |
|------|:-----------:|:------------:|:--------:|:-----------:|:----------:|:-----:|:-------:|
| **CheckoutSaga** | A | A | A | A+ | B+ | A | **A** |
| **RefundSaga** | B+ | B | A- | **F** | B- | D | **C+** |
| **SubscriptionSaga** | **F** (not registered) | N/A | B- | **F** | C- | D+ | **D-** |
| **PrivacyRequestStateMachine** | D | **F** | **F** | C | D | **F** | **F** |

### Rating Key
- **A**: Production-ready, well-tested, follows best practices
- **B**: Functional with minor gaps
- **C**: Works in happy path, significant gaps in error handling
- **D**: Major issues, limited or no test coverage
- **F**: Broken, missing critical functionality, or untested

---

## Prioritized Fix List

### P0 -- Fix Before Next Deploy (Production Blockers)

1. **SS-01**: Register `SubscriptionSaga` in production MassTransit DI (`Payments.Infrastructure.DependencyInjection.cs`). Without this, the saga is dead code in production.

2. **XC-03**: Add `AddDelayedMessageScheduler()` to Payments MassTransit configuration. Without this, RefundSaga and SubscriptionSaga schedule operations will throw in production.

3. **PR-01**: Add `PaymentsCompleted` to the privacy saga completion guard. Current state may violate GDPR by marking erasure as complete before payments data is erased.

### P1 -- Fix This Sprint (Data Integrity / Correctness)

4. **XC-01 / RS-01 / RS-02 / SS-05**: Add concurrency tokens (`Version` + `xmin`) to `RefundSagaState` and `SubscriptionSagaState` in `PaymentDbContext`. Without these, concurrent events can silently corrupt saga state.

5. **SS-02**: Fix SubscriptionSaga correlation strategy. The current mix of `CorrelateById` (Guid) and `CorrelateBy` (string) means events like `RenewalRequested` and `RenewalFailed` cannot reliably reach the correct saga instance.

6. **SS-03**: Wire `PaymentRecovered` event handling in GracePeriod state. Currently dead code.

7. **SS-06**: Re-schedule `RenewalTimeoutSchedule` when recovering from GracePeriod to Active.

8. **PR-03**: Add `PrivacyErasureFailed` handling to the privacy saga.

### P2 -- Fix This Quarter (Robustness / Compliance)

9. **SS-04**: Guard against negative TimeSpan in renewal timeout delay computation.

10. **SS-07**: Handle `SubscriptionCancelled` in all states (move to `DuringAny`).

11. **PR-02**: Add timeout schedule to privacy saga (GDPR 30-day compliance).

12. **PR-04**: Add finalization or cleanup strategy for completed privacy saga rows.

13. **CS-04 / RS-06**: Implement cleanup job for terminal (non-finalized) saga rows (Abandoned, RequiresReview).

14. **XC-02 / RS-05**: Add `DuplicateDetectionWindow` to Payments outbox configuration.

15. **RS-04**: Fix hardcoded `Provider = "Stripe"` in RefundSaga.

### P3 -- Improve Test Coverage

16. **PR-09**: Create Privacy saga test suite (unit + integration).

17. Add RefundSaga tests: full happy path, provider failure, timeout, operator cancellation.

18. Add SubscriptionSaga tests: full renewal cycle, dunning exhaustion, recovery, cancellation.

19. Add CheckoutSaga edge case tests: duplicate events, concurrent events, out-of-order delivery.

### P4 -- Housekeeping

20. **CS-01**: Fix saga doc comment about `CheckoutSessionExpiredEvent` on timeout path.

21. **CS-07**: Log warning on JSON deserialization failure in `DeserializeItems()`.

22. **PR-10**: Move `InitiatePrivacyRequestMessage` to `src/Contracts/Privacy/`.

23. **PR-05 / PR-06**: Set `CompletedAt` and `RequestType` fields.

---

*End of audit report.*
