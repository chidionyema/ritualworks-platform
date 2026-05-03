# ADR-0003: Checkout Saga Lives in Its Own Service (CheckoutOrchestrator-svc)

**Status:** Accepted
**Date:** 2026-05-02
**Deciders:** chidionyema

## Context

The current monolith implements checkout as `ProcessCheckoutCommandHandler` — a 550-line "god handler" with 10+ injected dependencies that:
- Validates products (Catalog)
- Reserves stock (Catalog)
- Creates Order + Payment in one DB transaction (Orders + Payments)
- Calls Stripe to create a checkout session
- Manages compensation (release stock if any step fails)
- Handles idempotency, race conditions, and recovery queue fallback

`event-integration-rationale.md` explicitly identifies this as the "god handler" anti-pattern and recommends a MassTransit saga state machine as the replacement.

When extracting Orders into its own service, two options exist:
1. **Saga lives in orders-svc** (alongside Order aggregate).
2. **Saga lives in its own service** (CheckoutOrchestrator-svc).

The choice has ripple effects: who owns the saga's database, who deploys saga upgrades, who's on-call for saga failures.

## Decision

**The checkout saga lives in its own service: `checkout-orchestrator-svc`.** It owns:
- `CheckoutDbContext` containing `CheckoutSagaState` + per-context outbox/inbox tables (using the existing `BoundedContextConsumerDefinition<T,TDb>` wiring).
- The `CheckoutSaga` MassTransit state machine (lifted from `src/Infrastructure/Messaging/Sagas/CheckoutSaga.cs`).
- Saga timeout schedules (e.g., 15-min payment expiry).
- The compensation orchestration logic.
- A `GetSagaStatus(orderId)` gRPC endpoint for ops/debug.

**It owns no business state.** Orders are still owned by orders-svc, payments by payments-svc, stock by catalog-svc. The orchestrator only holds correlation data + last-known status.

The saga reacts to events from Catalog, Orders, Payments and emits commands/events to coordinate them. All transitions are atomic at each end (consume-side EF outbox commits inbox + state + outgoing publishes in one local transaction).

## Options Considered

| Option | Pros | Cons | Verdict |
|---|---|---|---|
| **Saga in its own service (chosen)** | Clean separation: orchestrator has no business state. Saga deploys independently of any participant. orders-svc has zero saga code. | Adds a service. Cross-service correlation requires `SagaId` in every event (additive — already done). | **Chosen.** Matches MT's "saga as orchestrator with its own DB" pattern. |
| Saga in orders-svc | One fewer service. Saga state co-located with Order aggregate. | Makes orders-svc the de-facto orchestrator for Payments — exactly the distributed-monolith smell. orders-svc team becomes responsible for changes to payments-svc's saga interactions. | Rejected. The current monolith's pinning of saga to OrderDbContext was a pragmatic monolith choice, not a domain choice. |
| Saga in monolith forever (residual) | Zero risk during Phase 6. | Defeats the portfolio purpose — the saga IS the crown jewel demo. | Rejected as default. **Acceptable fallback** if Phase 6 fails after multiple attempts (see [05-risks.md § Risk 1](../05-risks.md#risk-1--the-checkout-saga-cant-extract-cleanly)). |
| Choreographed (no saga, just event chain) | No central state machine — pure event flow. | Compensation logic is scattered across consumers. Hard to query "what state is order X in?". State machine pattern is better understood. | Rejected. Choreography is appropriate for simpler flows (3 hops); checkout has too many failure modes. |

## Consequences

### Positive
- The saga is the **crown jewel portfolio artifact**. Real MT state machine with explicit compensation paths in its own service is exactly what consulting clients hire for.
- `make demo-saga-failure` (kill payments-svc mid-checkout, watch saga compensate) becomes a 2-minute Loom recording for the README.
- orders-svc, payments-svc, catalog-svc all stay focused on their own domain. None of them know about checkout flow.
- Saga state queryable via gRPC for ops tooling — distinguishing feature for a portfolio.

### Negative
- Phase 5 (saga build) is the highest-impact phase — see [05-risks.md § Risk 1](../05-risks.md#risk-1--building-the-saga-correctly-the-first-time). **Mitigation:** build the state machine in a `MassTransit.Testing` harness first and prove every transition + compensation path before wiring real RabbitMQ; the `SagaCompensationChaosTests` is the merge gate.
- Saga events must carry `SagaId` end-to-end. The reference monolith's `StockReleasedEvent` correlates by `OrderId` (fragile). **Mitigation:** in the new repo, every event the saga consumes carries `SagaId` from day 1, correlated exclusively by `SagaId`.
- One more service to operate, one more DB to manage. **Acceptable.**

### Neutral
- The saga's DB is small (just state rows + outbox/inbox). HA story is the same as any other service — Postgres replication + Vault dynamic creds.

## Notes

Saga compensation paths for the record:

| Failure point | Compensation |
|---|---|
| `StockReservationFailed` | Publish `OrderAbandoned`. orders-svc marks Order `Abandoned`. Notification email. |
| `PaymentSessionFailed` | Publish `ReleaseStock` + `OrderAbandoned`. catalog-svc releases stock. |
| `PaymentExpired` (15-min timeout) | Same as `PaymentSessionFailed`. |
| `PaymentAmountMismatch` (webhook reports wrong amount) | Mark Order `RequiresReview`, alert ops via Slack. |
| `ReleaseStock` itself fails | MT retry → DLQ → ops alert. Stock zombie possible — verified by chaos test. |

Reference: [01-architecture.md § The Saga](../01-architecture.md#the-saga-checkoutorchestrator)
