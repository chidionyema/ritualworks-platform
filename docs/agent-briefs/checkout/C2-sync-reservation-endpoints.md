# C2 — Synchronous reservation endpoints (catalog-svc + BFF passthrough) — ADR-004

## Goal

Add the synchronous reservation HTTP path: `POST /api/checkout/reservations` (201 / 409 fail-fast on insufficient stock) and `POST /api/checkout/reservations/{id}/confirm` (server-issued OrderId). This is the alternative to the async saga path — for UIs that need an immediate yes/no on stock, or want to "hold" inventory while the user enters payment details.

## Phase / blocks-on

Phase 1 (parallel with C1, C3, C4). Blocks-on: nothing — domain entity already exists.

## Inputs (read in order, all of them, before writing)

1. `docs/agent-briefs/checkout/README.md`.
2. `docs/agent-briefs/checkout-payments-gaps-spec.md`.
3. `src/Catalog/Catalog.Domain/` — find the `StockReservation` aggregate. Check that it has `Confirm(...)` and `Expire()` lifecycle methods AND a `ReservationStatus` enum (Pending, Confirmed, Expired). If `Confirm` or `Expire` is missing, file a blocker — do **not** add domain methods. The C3 brief audits this in parallel; either it's already there or both C2 and C3 file the same blocker.
4. `src/Catalog/Catalog.Application/Commands/` — list existing files. There may already be a `ReserveStockCommand.cs` for the saga path. **Do not collide with it** — your new commands belong in `src/Catalog/Catalog.Application/Commands/Reservations/` and should be named `CreateReservationCommand.cs` and `ConfirmReservationCommand.cs` to avoid the name clash.
5. `src/Catalog/Catalog.Domain/Interfaces/` — find the reservation repository contract (probably `IReservationRepository` or similar). Confirm it has `AddAsync`, `GetByIdAsync`, and an "atomically attempt to reserve stock" method. If missing, file a blocker.
6. `src/Catalog/Catalog.Api/Controllers/ProductsController.cs` — controller style template.
7. `src/Catalog/Catalog.Api/Program.cs` — confirm `AddControllers()` and auth are wired.
8. `src/BffWeb/BffWeb.Api/Controllers/SearchController.cs` — exact template for the BFF passthrough.
9. `src/BffWeb/BffWeb.Api/BackendClients.cs` — confirm `Catalog` constant exists. It does.
10. `tests/Catalog.Integration/CatalogWebAppFactory.cs` and `CatalogFlowsTests.cs` — test fixture pattern. Note the existing `[Collection("Catalog Integration")]` — your new test class must use it.
11. `src/Payments/Payments.Application/Common/IdempotencyKeyGenerator.cs` — for `X-Idempotency-Key` handling. **It's in Payments.Application** — this brief reuses an existing shared abstraction; if accessing it from Catalog violates the boundary rules, file a blocker. Most likely the platform has a `BuildingBlocks` version of the same generator; check `src/BuildingBlocks/` first.

## Deliverable

### Application layer

- `src/Catalog/Catalog.Application/Commands/Reservations/CreateReservationCommand.cs` — `record CreateReservationCommand(IReadOnlyList<ReservationItemDto> Items, string UserId, string? ClientIdempotencyKey) : IRequest<Result<ReservationDto>>` + handler. Handler attempts the atomic reservation; on stock-insufficient, returns `Result.Failure` with code `Reservation.InsufficientStock`. On idempotent-replay (same key), returns existing reservation with `IsExisting=true`.

- `src/Catalog/Catalog.Application/Commands/Reservations/ConfirmReservationCommand.cs` — `record ConfirmReservationCommand(Guid ReservationId, Guid OrderId, Guid SagaId, string UserId, string CustomerEmail) : IRequest<Result<ConfirmReservationResultDto>>` + handler. Calls `reservation.Confirm(orderId, sagaId)` (or whatever lifecycle method exists). Maps domain failures: `Reservation.Expired`, `Reservation.NotFound`, `Reservation.InvalidState`.

- `src/Catalog/Catalog.Application/DTOs/Reservations/ReservationDto.cs` — `record (Guid ReservationId, IReadOnlyList<ReservationItemDto> Items, DateTimeOffset ExpiresAt, bool IsExisting)`.

- `src/Catalog/Catalog.Application/DTOs/Reservations/ConfirmReservationResultDto.cs` — `record (Guid ReservationId, Guid OrderId, Guid SagaId)`.

- Validators for both commands (sibling files).

### API layer

`src/Catalog/Catalog.Api/Controllers/ReservationsController.cs`:

- `[Route("api/checkout/reservations")]`. Yes — under `/api/checkout/` not `/api/reservations/`. Matches monolith's URL exactly (frontends are written to it).
- `[HttpPost]` create — accepts `X-Idempotency-Key` header, returns 201 / 409 / 400. Anonymous-allowed (use a guest user id constant from existing platform if it has one — check `Catalog.Application` for `CheckoutConstants.GuestUserId` or similar).
- `[HttpPost("{reservationId:guid}/confirm")]` — `[Authorize]`. Pull `userId` and `customerEmail` from claims. Map domain errors:
  - `Reservation.Expired` → 410 Gone
  - `Reservation.NotFound` → 404
  - `Reservation.InvalidState` → 409
  - else → `result.ToActionResult()` (or whatever the platform's helper is — check Catalog's existing controllers).

### BFF passthrough

`src/BffWeb/BffWeb.Api/Controllers/ReservationsController.cs` — same shape as `SearchController.cs`. Routes:
- `POST /api/checkout/reservations` → `catalog-svc:/api/checkout/reservations`
- `POST /api/checkout/reservations/{id}/confirm` → `catalog-svc:/api/checkout/reservations/{id}/confirm`

Forwards request body, query, and `Authorization` + `X-Idempotency-Key` headers verbatim.

### Tests

`tests/Catalog.Unit/Reservations/CreateReservationHandlerTests.cs` + `ConfirmReservationHandlerTests.cs` — 4-5 unit cases each.

`tests/Catalog.Integration/ReservationEndpointTests.cs` — `[Collection("Catalog Integration")]`. Cases:
- `CreateReservation_returns_201_when_stock_available`
- `CreateReservation_returns_409_when_stock_insufficient`
- `CreateReservation_with_repeated_idempotency_key_returns_same_reservation`
- `ConfirmReservation_returns_200_with_orderId_when_pending`
- `ConfirmReservation_returns_410_when_expired`
- `ConfirmReservation_returns_404_when_not_found`

## Acceptance

```bash
dotnet build HaworksPlatform.sln -c Release
dotnet test tests/Catalog.Unit -c Release
dotnet test tests/Catalog.Integration -c Release --filter "FullyQualifiedName~ReservationEndpointTests"
dotnet test tests/Catalog.Integration -c Release   # full suite — no regressions
```

All green.

## Hard stops

- Do **NOT** modify `src/Catalog/Catalog.Infrastructure/DependencyInjection.cs`. C3 owns that file in this phase. MediatR scans the application assembly; your handlers register automatically. If you think you need an Infrastructure-side DI change, file a blocker.
- Do **not** modify `src/Catalog/Catalog.Application/Commands/ReserveStockCommand.cs` or any pre-existing commands. Add new files in the `Reservations/` subfolder.
- Do **not** modify the `StockReservation` aggregate. Use only the public API it already exposes.
- Do **not** modify any other service (`src/Payments/`, `src/Orders/`, etc.).
- Do **not** modify the BFF Program.cs — controllers auto-discover.
- Do **not** add a new `BackendClients.Catalog` entry — it already exists.
- Do **not** add CSRF middleware.

## Done-report

Standard format. Specifically confirm:
- New commands live in `Commands/Reservations/`, not in the root `Commands/` (no clash with the saga's `ReserveStockCommand`).
- The error-code mapping for `Reservation.Expired` → 410 is in place.
- `[Collection("Catalog Integration")]` is on the new test class.
- `Catalog.Infrastructure/DependencyInjection.cs` was NOT touched.
