# Platform completion — Phase A (auth) + Phase B (reservation lifecycle)

**Status: Complete.** All phases (A1–A4 auth, B1–B3 reservation lifecycle) shipped and merged to main.

**Source incidents:** the C1 done-report flagged that `payments-svc` has no JWT validation despite `[Authorize]` attributes, and the same is true of every backend service in the platform. The C2 and C3 done-reports flagged that `OrderStockReservation` is half-built relative to ADR-004's design.

---

## Phase A — JWT authentication + user-id propagation

### Goal

Every backend service validates JWT bearer tokens, the BFF forwards user identity downstream, and `[Authorize]`-decorated endpoints actually enforce auth in production (currently they 500 at request time because no auth scheme is registered).

### What's broken right now

- `BuildingBlocks.Extensions.ServiceDefaults` adds OTel + service discovery + resilience but **not** authentication. Confirmed by reading the file.
- `payments-svc`, `catalog-svc`, `orders-svc` all call `app.UseAuthentication()` without ever registering a scheme via `builder.Services.AddAuthentication(...).AddJwtBearer(...)`.
- `BffWeb.Api/Program.cs` has the same gap. None of the BFF controllers extract user id from claims today.
- `identity-svc` is the only service with real auth wiring (it's the issuer).

### Design

```
┌──────────┐  Bearer JWT       ┌────────┐  X-User-Id header  ┌──────────────┐
│ Browser  │ ───────────────►  │  BFF   │ ───────────────►   │ payments-svc │
└──────────┘                   └────────┘                    │ orders-svc   │
                               validates                     │ catalog-svc  │
                               JWT, sets                     │ search-svc   │
                               X-User-Id from                │ checkout-svc │
                               NameIdentifier                └──────────────┘
                                                             validates JWT
                                                             too (defence in
                                                             depth — backends
                                                             reachable via
                                                             flycast from the
                                                             BFF, but trust
                                                             nothing implicitly)
```

Defence-in-depth — both BFF and backend services validate. Backends that need a user id read it from `X-User-Id` header (BFF-set), not from the bearer token's claims, so handlers don't have to know about JWT internals. The bearer token is forwarded too, so future scope claims (`subscriptions:write`, etc.) can be checked at the backend if needed.

### Scope of work

**Single shared extension** — `BuildingBlocks.Extensions.AuthenticationExtensions.AddPlatformAuthentication`:

- Reads `Jwt:Issuer`, `Jwt:Audience`, `Jwt:SigningKeyPem` from configuration (Identity already publishes those secrets via Fly).
- Registers `JwtBearerDefaults.AuthenticationScheme` with the right validation parameters.
- Registers a default authorisation policy (`RequireAuthenticatedUser`) so `[Authorize]` is meaningful out of the box.
- Test fixtures keep using `TestAuthenticationHandler` (already wired in `BuildingBlocks.Testing` per Gemini's C1 work).

**Per-service wiring** — each backend service calls `builder.Services.AddPlatformAuthentication(builder.Configuration)` from its `Program.cs` once. ~5 services × 1 line each.

**BFF user-id propagation** — a `DelegatingHandler` named `UserIdentityForwardingHandler`:

1. Reads `User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")`.
2. Sets it as `X-User-Id` on the outbound request to the backend.
3. Registered on every named HttpClient in `BackendClients` via `.AddHttpMessageHandler<UserIdentityForwardingHandler>()`.

**Header-based user-id reader** — small helper in BuildingBlocks: `HttpContext.GetForwardedUserId()` extension that reads the header. Backend controllers that need user-id (subscriptions, future personalised search, checkout reservation confirm, etc.) call this instead of `User.FindFirstValue`.

**Migrate existing `[Authorize]` controllers** — `OrdersController`, `ProductReviewsController`, the new `SubscriptionsController`. They keep `[Authorize]` (now meaningful) and switch to `HttpContext.GetForwardedUserId()` when they need the user.

**Tests** — integration tests in each service add a "401 without token" + "200 with token" pair. The platform's existing `TestAuthenticationHandler` covers the green path.

### Briefs (Gemini-CLI ready)

| Brief | Service | Effort |
| --- | --- | --- |
| A1 | `BuildingBlocks` shared `AddPlatformAuthentication` + `UserIdentityForwardingHandler` + `GetForwardedUserId` extension | small |
| A2 | Per-service wiring (5 backends × `Program.cs` one-liner + integration test addition) | small (parallelizable across services) |
| A3 | BFF: register `UserIdentityForwardingHandler` on every named HttpClient + add `[Authorize]` to controllers that should require auth | small |
| A4 | Update existing `SubscriptionsController` (and any other user-aware endpoint) to use `GetForwardedUserId()` instead of `User.FindFirstValue` | small |

A1 blocks A2/A3/A4. A2/A3/A4 are parallelizable.

### Success criteria

- Hitting `https://ritualworks-bffweb.fly.dev/api/subscriptions/status` without a JWT returns 401 (today: would 500 because no auth scheme).
- With a valid JWT, BFF forwards `X-User-Id` and payments-svc reads it. Subscriptions endpoint serves the right user's record.
- Each backend service's integration suite adds a 401-without-token assertion that passes.

---

## Phase B — reservation lifecycle (unblocks C2 + C3)

### Goal

Bring `StockReservation` up to ADR-004's design: a real `Pending → Confirmed | Expired` aggregate with its own table, sweeper-driven expiry, and atomic stock decrement on create.

### What's broken right now

- `Catalog.Domain/OrderStockReservation.cs` is a post-order tracker — created with `(orderId, itemsJson)` after the order exists. ADR-004 wants a pre-order **handle** — created from `(items, userId)`, returns `ReservationId`, holds inventory for 15 minutes, then either confirms (allocates an `OrderId`) or expires.
- No `Status` enum, no `ExpiresAt`, no `Confirm`, no `Expire`. Only `MarkReleased`.
- `IStockService` has no `ReleaseStockAsync(IEnumerable<StockReservationItem>, CancellationToken)` overload — the C3 sweeper needs it.
- The C2 brief assumed `StockReservation` (no `Order` prefix), the platform calls it `OrderStockReservation`. Either rename it or live with the prefix.

### Design

**New aggregate** — `Catalog.Domain.StockReservation` (or rename the existing `OrderStockReservation`):

```csharp
public class StockReservation : AuditableEntity
{
    public Guid? OrderId { get; private set; }      // null until Confirm
    public Guid? SagaId { get; private set; }       // null until Confirm
    public string UserId { get; private set; }
    public string ItemsJson { get; private set; }
    public ReservationStatus Status { get; private set; }
    public DateTime ReservedAt { get; private set; }
    public DateTime ExpiresAt { get; private set; }
    public DateTime? ConfirmedAt { get; private set; }
    public DateTime? ExpiredAt { get; private set; }

    public static StockReservation Create(string userId, string itemsJson, TimeSpan ttl) { … }
    public bool Confirm(Guid orderId, Guid sagaId) { … }   // pending → confirmed
    public bool Expire() { … }                              // pending → expired (idempotent guard)
}

public enum ReservationStatus { Pending, Confirmed, Expired, Released }
```

**Repository changes:**

- `IReservationRepository.AtomicallyReserveAsync(items, userId, ttl)` — wraps an EF transaction that decrements `Product.StockQuantity` AND inserts the reservation, all-or-nothing.
- `GetByIdTrackedAsync(reservationId)` — for confirm path.
- `ListExpiredAsync(now, batchSize)` — for sweeper.

**`IStockService.ReleaseStockAsync` overload:**

```csharp
Task ReleaseStockAsync(IEnumerable<StockReservationItem> items, CancellationToken ct);
```

Increments `Product.StockQuantity` by `item.Quantity` for each. Used by both the confirm-failed-cleanup path and the C3 sweeper.

**Migration:** `AddStockReservationLifecycle` — adds `Status`, `ExpiresAt`, `ConfirmedAt`, `ExpiredAt`, `UserId` columns and an index on `(Status, ExpiresAt)` for the sweeper query.

**Existing `OrderStockReservation` usage** — audit every callsite. The current entity is created from `(orderId, itemsJson)` so it's tied to an existing order. The new lifecycle works for both flows: saga path keeps creating reservations after the order (status starts `Confirmed` directly via a `CreateConfirmed` factory); sync path creates with `Pending` and confirms later.

### Briefs (Gemini-CLI ready)

| Brief | Effort |
| --- | --- |
| B1 | Catalog domain: rename to `StockReservation`, add `ReservationStatus`, `Confirm`, `Expire`, `Create`/`CreateConfirmed` factories. Migration. Update existing saga callsites that use the old `MarkReleased` shape. | medium |
| B2 (= the originally-deferred C2) | `POST /api/checkout/reservations` + `POST /api/checkout/reservations/{id}/confirm` endpoints, BFF passthrough, tests. | small (after B1) |
| B3 (= the originally-deferred C3) | `ReservationSweeperService` background hosted service. Already specified in `docs/agent-briefs/checkout/C3-reservation-sweeper.md` — that brief becomes accurate once B1 lands. | small (after B1) |

B1 blocks B2 and B3. B2 and B3 are parallelizable.

### Success criteria

- `POST /api/checkout/reservations` with valid items returns 201 + reservation id, holds inventory.
- `POST /api/checkout/reservations/{id}/confirm` with a confirmed authenticated user returns 200 + new orderId. Stock is now firmly committed.
- Letting a reservation expire (15 min TTL) returns inventory to the catalog, observable via product stock query.
- The C3 sweeper test from the existing brief passes.

---

## Sequencing

```
Phase A (small, unblocks user-aware endpoints)
  ├─ A1  BuildingBlocks helpers                      [SMALL — sequential blocker]
  ├─ A2  Per-service wiring                          [SMALL — parallel across 5 services]
  ├─ A3  BFF UserIdentityForwardingHandler           [SMALL — parallel with A2]
  └─ A4  Migrate SubscriptionsController etc.        [SMALL — parallel with A2]

Phase B (medium, unblocks reservation flow)
  ├─ B1  Reservation domain + migration              [MEDIUM — sequential blocker]
  ├─ B2  Sync reservation endpoints (was C2)         [SMALL — parallel after B1]
  └─ B3  ReservationSweeperService (was C3)          [SMALL — parallel after B1]
```

**Phase A first.** Without it, the just-merged C1 endpoints serve unauthenticated traffic in production. Phase B is operationally important (reservation leaks compound under traffic) but not security-critical.

---

## Out of scope

- A real OAuth/OIDC integration (Identity-svc is the JWT issuer; that's enough).
- Refresh-token flow if not already in identity-svc.
- Per-route scope claims like `subscriptions:write` — the design supports them but ship the basic auth first.
- A real OpenTelemetry-backed `IReservationMetrics` (`NullReservationMetrics` is fine for v1).
