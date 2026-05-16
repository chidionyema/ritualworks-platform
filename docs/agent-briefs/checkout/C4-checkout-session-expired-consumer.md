# C4 — `CheckoutSessionExpiredConsumer` (orders-svc)

## Goal

Wire the missing consumer for Stripe's `checkout.session.expired` webhook. The Payments service already publishes `CheckoutSessionExpiredEvent` but nothing consumes it, so when a Stripe session expires the order stays in whatever state it was in (`Pending`/`AwaitingPayment`) instead of moving to `Expired`. Orders-side consumer marks the order Expired idempotently and triggers stock release.

## Phase / blocks-on

Phase 1 (parallel with C1, C2, C3). Blocks-on: nothing — the event already exists.

## Inputs (read in order, all of them, before writing)

1. `docs/agent-briefs/checkout/README.md`.
2. `docs/agent-briefs/checkout-payments-gaps-spec.md`.
3. `src/Contracts/Payments/CheckoutSessionExpiredEvent.cs` — confirm shape. The monolith's payload has `OrderId, PaymentId, SessionId, Provider`. If this event doesn't exist in `src/Contracts/Payments/`, file a blocker — `StripeWebhookProcessor` definitely publishes it (see `src/Payments/Payments.Infrastructure/Stripe/StripeWebhookProcessor.cs HandleCheckoutSessionExpiredAsync`), so the contract record must be present somewhere. Search for it.
4. `src/Payments/Payments.Infrastructure/Stripe/StripeWebhookProcessor.cs` — read `HandleCheckoutSessionExpiredAsync` to confirm what fields are populated on publish.
5. `src/Orders/Orders.Domain/` — find the `Order` aggregate and `OrderStatus` enum. Confirm there's an `Expired` value, and that the aggregate has a method like `MarkExpired(...)` or `MarkStockReleased(...)`. If neither exists, file a blocker — adding domain methods is out of scope.
6. `src/Orders/Orders.Domain/Interfaces/IOrderRepository.cs` — confirm there's an atomic "mark expired" repository method (the monolith uses `MarkStockReleasedAsync(orderId, OrderStatus.Expired, "checkout_session_expired", ct)` which returns `false` if the order is already in a terminal state). If not present, file a blocker.
7. `src/Orders/Orders.Application/Consumers/PaymentCompletedConsumer.cs` — your **exact** style template (logger, scope, idempotency check, structured logging).
8. `src/Orders/Orders.Infrastructure/DependencyInjection.cs` — find the existing `mt.AddConsumer<...>()` calls to know where to add yours.
9. `tests/Orders.Integration/` (or whatever the orders-svc integration test project is called — find it). Note any existing `[Collection]` attribute. If the existing tests use `IClassFixture<OrdersWebAppFactory>` directly, add a shared collection (mirror the Catalog/Payments pattern from earlier in the project) so your new test class doesn't re-spin the Postgres container in parallel.

## Deliverable

### Consumer

`src/Orders/Orders.Application/Consumers/CheckoutSessionExpiredConsumer.cs`:

- `internal sealed`, `IConsumer<CheckoutSessionExpiredEvent>`.
- Logger + repository + (optional) `IDomainEventPublisher` (only if the platform's Order aggregate raises a follow-on `OrderExpiredEvent` — check the aggregate; if no, skip the publisher injection).
- `Consume`:
  1. Open a logging scope with `OrderId`, `PaymentId`, `SessionId`.
  2. Look up order via `IOrderRepository.GetByIdAsync` (or equivalent).
  3. If order is null → log warning `"Order {OrderId} not found for expired session {SessionId}"` and return (idempotent — event arrived for an order that no longer exists; nothing to do).
  4. If order is already in a terminal status (`Expired`, `Completed`, `Cancelled`) → log info `"Order {OrderId} already in terminal status {Status}, skipping"` and return.
  5. Build the stock-reservation-items list from `order.OrderItems` (each item: `ProductId`, `ProductName ?? "Unknown"`, `Quantity`).
  6. Atomically mark order expired: `var wasMarked = await _orderRepository.MarkStockReleasedAsync(order.Id, OrderStatus.Expired, "checkout_session_expired", ct)`. If `false` (lost a race with another consume), log info and return.
  7. Publish a `StockReleaseRequestedEvent` (or whatever event the platform uses to signal catalog to release stock — check `src/Contracts/`). If no such event exists, file a blocker; do not invent one.
- Mirror the monolith's `src/Infrastructure/Messaging/Consumers/CheckoutSessionExpiredConsumer.cs`. The order-of-operations matters: atomic mark first, then publish stock-release event, never the other way round.

### DI registration

In `src/Orders/Orders.Infrastructure/DependencyInjection.cs`, inside the existing `services.AddMassTransit(mt => { … })` block, add **one line** alongside the existing consumer registrations:

```csharp
mt.AddConsumer<CheckoutSessionExpiredConsumer>();
```

Match the existing style (with or without `ConsumerDefinition`, depending on what the others use). Do not change anything else.

### Tests

`tests/Orders.Integration/CheckoutSessionExpiredConsumerTests.cs` — `[Collection("Orders Integration")]` (add the collection if it doesn't exist; mirror the Catalog/Payments shared-collection pattern).

Cases:
- `Consume_marks_order_Expired_when_pending` — seed an order in `Pending`, publish `CheckoutSessionExpiredEvent`, poll until order moves to `Expired`. Assert.
- `Consume_is_idempotent_for_terminal_order` — seed an order in `Completed`, publish event, assert order stays `Completed` (no exception).
- `Consume_is_noop_for_unknown_order` — publish event with a random orderId, assert no consume fault.
- `Consume_publishes_stock_release_event` — assert downstream `StockReleaseRequestedEvent` (or whatever the platform's event is) lands on the harness for the matching order.

## Acceptance

```bash
dotnet build HaworksPlatform.sln -c Release
dotnet test tests/Orders.Unit -c Release
dotnet test tests/Orders.Integration -c Release --filter "FullyQualifiedName~CheckoutSessionExpiredConsumerTests"
dotnet test tests/Orders.Integration -c Release   # full suite — no regressions
```

All green.

## Hard stops

- Do **not** modify any file outside `src/Orders/`, `src/Contracts/Payments/CheckoutSessionExpiredEvent.cs` (read-only — only flagged if it's missing), and `tests/Orders.*`.
- Do **not** modify the `Order` aggregate or `OrderStatus` enum. Use only the public API.
- Do **not** add a new repository method to `IOrderRepository`. Use what's there. If the atomic "mark expired" method genuinely doesn't exist, file a blocker — adding domain interface methods is a separate change with cross-cutting impact.
- Do **not** modify Stripe webhook processor or any payments-side code. The publish path is already wired.
- Do **not** publish `OrderExpiredEvent` if no such event exists in contracts. Don't invent contracts.

## Done-report

Standard format. Specifically confirm:
- The consumer is `internal sealed`.
- Atomic-mark-before-stock-release ordering is preserved.
- All four test cases pass.
- Whether you needed to add `[Collection("Orders Integration")]` (and the collection definition file) — call it out so the reviewer knows.
