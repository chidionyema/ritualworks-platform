# Checkout & Payments — gap-fill spec

**Status:** signed off 2026-05-08 — fill the four real gaps surfaced by the deep migration review against the monolith. Skip the auto-detect generic webhook (provider-specific endpoints are sufficient).
**Implementer:** Gemini CLI agents working brief-by-brief from `docs/agent-briefs/checkout/`.
**Reviewer:** Claude / user, between phases.
**Source of the gap list:** the 2026-05-08 deep-review chat, summarised below.

---

## What's being filled

| # | Gap | Severity | Where | Effort |
| - | --- | --- | --- | --- |
| C1 | Subscription HTTP endpoints (`GET /api/subscriptions/status`, `POST /api/subscriptions/create-checkout-session`) — backend services exist, no controller exposes them | HIGH | `src/Payments/Payments.Api/`, BFF passthrough | small |
| C2 | Synchronous reservation endpoints (`POST /api/checkout/reservations`, `POST /api/checkout/reservations/{id}/confirm`) per ADR-004 — alternative to the saga path | HIGH | `src/Catalog/Catalog.Api/`, BFF passthrough | medium |
| C3 | `ReservationSweeperService` `BackgroundService` — releases expired reservations every minute; without it, abandoned carts leak inventory | HIGH | `src/Catalog/Catalog.Infrastructure/BackgroundServices/` | small |
| C4 | `CheckoutSessionExpiredConsumer` in orders-svc — handles Stripe's `checkout.session.expired` webhook so abandoned orders move to `Expired` and stock is released | MEDIUM | `src/Orders/Orders.Application/Consumers/` | small |

**Total estimated effort if executed in parallel:** ~1 dev-day end-to-end (~4 dev-days serial).

---

## Already migrated (verified — NOT gaps)

These were on the preliminary punch-list but a code-level read confirms they exist on the platform side:

| Monolith feature | Platform location |
| --- | --- |
| `PaymentCompletedConsumer`, `OrderPaymentFailedConsumer`, `OrderPaymentStatusConsumer` (orders-side) | `src/Orders/Orders.Application/Consumers/PaymentCompletedConsumer.cs` + `PaymentSessionFailedConsumer.cs` + `StockReservationFailedConsumer.cs` |
| `CheckoutNotificationConsumer` (single-class consumer of 5 events for SignalR push) | Split (cleanly) across `src/BffWeb/BffWeb.Api/SignalR/SagaStepBridgeConsumers.cs` (5 small bridges) + `PaymentSessionCreatedConsumer.cs` |
| `PaymentVerifiedConsumer` (monolith body is "log + TODO email + TODO metrics") | The platform's `PaymentWebhookValidatedConsumer` is the validation path; the monolith consumer is a stub. Not a gap. |
| `StockReservation` domain entity | Catalog migration `20260503232850_AddProductMetadataAndStockReservation.cs` ✓ |

---

## Architectural placement

| Brief | Service | Why there |
| --- | --- | --- |
| C1 | `payments-svc` | Subscription is payment-domain; `ISubscriptionManager` already lives in `Payments.Application` |
| C2 | `catalog-svc` | Reservation is catalog-domain (atomic stock decrement against catalog DB); the `StockReservation` aggregate already lives in `Catalog.Domain` |
| C3 | `catalog-svc` | Same — sweeper queries `StockReservations` table in catalog DB and calls `IStockService.ReleaseStockAsync` |
| C4 | `orders-svc` | `CheckoutSessionExpiredEvent` updates the **order**'s status to `Expired`. Per ADR-0001 (bounded contexts), order mutations belong to orders-svc |

The **BFF** gets a passthrough route for C1 (`/api/subscriptions/*`) and C2 (`/api/checkout/reservations*`). C3 and C4 are internal — no BFF surface.

---

## Conflict analysis (parallel execution)

All four briefs touch disjoint files with one watch-out:

```
C1 ─ src/Payments/Payments.Application/{Commands,Queries,DTOs}/Subscriptions/  [NEW]
   ─ src/Payments/Payments.Api/Controllers/SubscriptionsController.cs           [NEW]
   ─ src/BffWeb/BffWeb.Api/Controllers/SubscriptionsController.cs               [NEW]
   ─ tests/Payments.Unit + tests/Payments.Integration                           [NEW]

C2 ─ src/Catalog/Catalog.Application/Commands/Reservations/                     [NEW]
   ─ src/Catalog/Catalog.Api/Controllers/ReservationsController.cs              [NEW]
   ─ src/BffWeb/BffWeb.Api/Controllers/ReservationsController.cs                [NEW]
   ─ tests/Catalog.Unit + tests/Catalog.Integration                             [NEW]

C3 ─ src/Catalog/Catalog.Application/Interfaces/IReservationMetrics.cs          [NEW]
   ─ src/Catalog/Catalog.Infrastructure/BackgroundServices/ReservationSweeperService.cs [NEW]
   ─ src/Catalog/Catalog.Infrastructure/DependencyInjection.cs                  [MODIFY — small]
   ─ tests/Catalog.Integration                                                  [NEW]

C4 ─ src/Contracts/Payments/CheckoutSessionExpiredEvent.cs                      [VERIFY exists; if not, NEW]
   ─ src/Orders/Orders.Application/Consumers/CheckoutSessionExpiredConsumer.cs  [NEW]
   ─ src/Orders/Orders.Infrastructure/DependencyInjection.cs                    [MODIFY — small]
   ─ tests/Orders.Integration                                                   [NEW]
```

**Watch-out:** C2 must NOT modify `src/Catalog/Catalog.Infrastructure/DependencyInjection.cs` (C3 owns that file in this phase). C2's command handlers register via MediatR's assembly scanning — no Infrastructure-side DI change should be needed. The C2 brief enforces this with a hard stop.

**Merge order (when all four done-reports come back):** C4 → C3 → C2 → C1. C3 and C2 both touch catalog code; C3 first means its DI edit lands cleanly before C2's app/api files. No conflicts expected even if order is swapped — this is just lowest-risk.

---

## Design decisions (locked)

| Topic | Decision |
| --- | --- |
| Auth model for subscription endpoints | JWT-bearer; user id from `User.FindFirstValue(ClaimTypes.NameIdentifier)` (matches monolith). Anonymous returns 401. |
| Auth model for reservation endpoints | Same — authenticated user only for `confirm` (server-issued OrderId requires email claim from the JWT, per ADR-004 phase 4). `POST /api/checkout/reservations` may accept a guest user-id constant if the existing platform supports it; agent verifies. |
| Idempotency on reservation create | `X-Idempotency-Key` header — same SHA256 namespace pattern as monolith (UserId + clientKey). Reuse the platform's existing `IIdempotencyKeyGenerator` (already migrated to `Payments.Application.Common`). |
| Sweeper interval | 1 minute (matches monolith). Batch size 200. Configurable via `ReservationSweeperOptions`. |
| Failed sweep behaviour | Aggregate `Expire()` first, stock release second; partial failure leaves the reservation Pending and re-tryable on the next sweep (no half-released state). |
| `CheckoutSessionExpiredConsumer` idempotency | Atomic `MarkStockReleasedAsync` repository method (returns false if order is already in a terminal state — skip). |
| BFF passthrough auth | The BFF already validates JWT before forwarding; the controller just relays headers + body. No new auth surface. |

---

## Test contracts

Each brief's Acceptance section requires:
- `dotnet build HaworksPlatform.sln -c Release` — clean.
- `dotnet test tests/<Service>.Unit -c Release` — green, including new unit tests.
- `dotnet test tests/<Service>.Integration -c Release` — green, including new integration tests using the existing `[Collection("<Service> Integration")]` shared fixture (xUnit collections are already in place from the search-service-spec session).

---

## Out of scope

- Generic auto-detect webhook endpoint + providers list — provider-specific endpoints already cover production needs.
- Migration of monolith-only operational endpoints (relay-pause/resume etc.).
- Subscription cancellation endpoint — not in monolith either; deferred until a real user need.

---

## Sign-off (2026-05-08)

| Question | Decision |
| --- | --- |
| Run all 4 briefs in parallel via worktrees? | Yes |
| Skip generic webhook auto-detect? | Yes |
| BFF passthrough for C1 + C2? | Yes |
| Add `ReservationSweeperOptions` for sweep interval / batch size? | Yes (minimal — section name `Reservations:Sweeper`) |
