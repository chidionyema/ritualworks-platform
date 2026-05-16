# B1 — Catalog domain: refactor OrderStockReservation → StockReservation lifecycle

## Goal

Bring `OrderStockReservation` up to ADR-004's design: a real `Pending → Confirmed | Expired` aggregate with `ExpiresAt`, sweeper-driven expiry, and an atomic-stock-decrement-on-create repository method. The current entity is a post-order tracker; this brief turns it into a pre-order handle (while keeping the saga path working).

## Phase / blocks-on

Phase B. **Sequential blocker for B2 and B3.** Phase A must be fully merged first.

## Inputs (read in order)

1. `docs/agent-briefs/platform/README.md`.
2. `docs/agent-briefs/platform-completion-spec.md` — Phase B in full.
3. `src/Catalog/Catalog.Domain/OrderStockReservation.cs` — the aggregate to refactor.
4. `src/Catalog/Catalog.Domain/Interfaces/` — list everything; identify the reservation repository contract.
5. `src/Catalog/Catalog.Infrastructure/Repositories/` — the repository impl.
6. `src/Catalog/Catalog.Infrastructure/CatalogDbContext.cs` — the EF model setup for reservations.
7. Every callsite of `OrderStockReservation` in the platform: `grep -rn "OrderStockReservation" src/Catalog/`. Saga-path callsites need to keep working — most likely a `CreateConfirmed` factory shortcut so existing flows don't change behaviour.
8. `src/Catalog/Catalog.Application/Interfaces/IStockService.cs` — the missing `ReleaseStockAsync(IEnumerable<StockReservationItem>)` overload goes here. Read the existing methods.
9. `src/Contracts/Catalog/StockReservedEvent.cs` — for the `StockReservationItem` shape.

## Deliverable

### Domain refactor

`src/Catalog/Catalog.Domain/StockReservation.cs` (rename file; rename class):

```csharp
public class StockReservation : AuditableEntity
{
    protected StockReservation() : base() { }

    private StockReservation(Guid id, string userId, string itemsJson, DateTime expiresAt, ReservationStatus initialStatus) : base()
    {
        Id = id;
        UserId = userId;
        ItemsJson = itemsJson;
        ReservedAt = DateTime.UtcNow;
        ExpiresAt = expiresAt;
        Status = initialStatus;
    }

    public string UserId { get; private set; } = string.Empty;
    public Guid? OrderId { get; private set; }       // null until Confirm
    public Guid? SagaId { get; private set; }        // null until Confirm
    public string ItemsJson { get; private set; } = "[]";
    public ReservationStatus Status { get; private set; }
    public DateTime ReservedAt { get; private set; }
    public DateTime ExpiresAt { get; private set; }
    public DateTime? ConfirmedAt { get; private set; }
    public DateTime? ExpiredAt { get; private set; }
    public DateTime? ReleasedAt { get; private set; }
    public string? ReleaseReason { get; private set; }

    /// <summary>Pre-order handle (B2's sync flow uses this).</summary>
    public static StockReservation Create(string userId, string itemsJson, TimeSpan ttl)
        => new(Guid.NewGuid(), userId, itemsJson, DateTime.UtcNow.Add(ttl), ReservationStatus.Pending);

    /// <summary>Saga path: skip the Pending stage, go straight to Confirmed.</summary>
    public static StockReservation CreateConfirmed(Guid orderId, Guid sagaId, string userId, string itemsJson)
    {
        var r = new StockReservation(Guid.NewGuid(), userId, itemsJson, DateTime.UtcNow.AddYears(1), ReservationStatus.Confirmed);
        r.OrderId = orderId;
        r.SagaId  = sagaId;
        r.ConfirmedAt = DateTime.UtcNow;
        return r;
    }

    public bool Confirm(Guid orderId, Guid sagaId)
    {
        if (Status != ReservationStatus.Pending) return false;
        if (DateTime.UtcNow > ExpiresAt) return false;
        Status = ReservationStatus.Confirmed;
        OrderId = orderId; SagaId = sagaId;
        ConfirmedAt = DateTime.UtcNow;
        LastModifiedDate = DateTime.UtcNow;
        return true;
    }

    public bool Expire()
    {
        if (Status != ReservationStatus.Pending) return false;
        Status = ReservationStatus.Expired;
        ExpiredAt = DateTime.UtcNow;
        LastModifiedDate = DateTime.UtcNow;
        return true;
    }

    public void MarkReleased(string reason)
    {
        if (ReleasedAt.HasValue) return;
        ReleasedAt = DateTime.UtcNow;
        ReleaseReason = reason;
    }
}

public enum ReservationStatus { Pending, Confirmed, Expired }
```

Drop the old `OrderStockReservation` class (file rename — git tracks).

### Repository

`src/Catalog/Catalog.Domain/Interfaces/IReservationRepository.cs` (or wherever the existing contract lives):

- Keep existing methods.
- Add `Task<StockReservation> CreateReservationAsync(string userId, IReadOnlyList<StockReservationItem> items, TimeSpan ttl, CancellationToken ct)` — wraps an EF transaction that **atomically** decrements `Product.StockQuantity` AND inserts the reservation. Throws `InsufficientStockException` (use existing exception type or add one) if any product would go negative.
- Add `Task<StockReservation?> GetByIdTrackedAsync(Guid id, CancellationToken ct)`.
- Add `Task<IReadOnlyList<StockReservation>> ListExpiredAsync(DateTime now, int batchSize, CancellationToken ct)` — used by B3 sweeper. Uses `(Status, ExpiresAt)` index for efficiency.

Implement in the repository class.

### IStockService overload

`src/Catalog/Catalog.Application/Interfaces/IStockService.cs`:

```csharp
Task ReleaseStockAsync(IEnumerable<StockReservationItem> items, CancellationToken ct = default);
```

Implement: increments `Product.StockQuantity` for each item. EF transaction.

### Migration

`AddStockReservationLifecycle`:

- Renames table `OrderStockReservations` → `StockReservations`.
- Adds columns: `UserId varchar(450)`, `Status int default 0`, `ExpiresAt timestamptz`, `ConfirmedAt timestamptz null`, `ExpiredAt timestamptz null`, `OrderId uuid null` (was non-null), `SagaId uuid null`.
- Index: `IX_StockReservations_Status_ExpiresAt` on `(Status, ExpiresAt)` — sweeper relies on it.
- Backfill any existing rows: set `Status = 1 (Confirmed)`, `UserId = ''` (or `'<unknown>'`), `ExpiresAt = ReservedAt + interval '1 year'` so they don't get swept.

EF migration command: `dotnet ef migrations add AddStockReservationLifecycle --project src/Catalog/Catalog.Infrastructure --startup-project src/Catalog/Catalog.Api --context CatalogDbContext`.

### Saga callsite update

The CheckoutSaga (or wherever the existing platform creates an `OrderStockReservation`) must switch to `StockReservation.CreateConfirmed(orderId, sagaId, userId, itemsJson)`. Same observable behaviour — reservation lands in the DB confirmed, no breakage.

`grep -rn "OrderStockReservation" src/` to find all callsites.

### Tests

`tests/Catalog.Unit/Domain/StockReservationTests.cs`:

- `Create_starts_in_Pending_with_ExpiresAt_after_now`
- `Confirm_transitions_Pending_to_Confirmed`
- `Confirm_returns_false_if_already_confirmed`
- `Confirm_returns_false_if_already_expired`
- `Expire_transitions_Pending_to_Expired`
- `Expire_returns_false_if_confirmed`
- `CreateConfirmed_starts_in_Confirmed_with_orderId_set` — the saga-shortcut.

Plus update existing Catalog integration tests if any assertion on `OrderStockReservation` shape needs adjusting. **Existing saga tests must still pass** — that's the load-bearing acceptance gate.

## Acceptance

```bash
dotnet build HaworksPlatform.sln -c Release
dotnet test tests/Catalog.Unit -c Release
dotnet test tests/Catalog.Integration -c Release   # full suite — saga path must not regress
```

All green.

## Hard stops

- Do **NOT** add HTTP endpoints (B2's territory).
- Do **NOT** add the sweeper (B3's territory).
- Do **NOT** modify other services' code (`src/Payments/`, `src/Orders/`, etc.).
- Do **NOT** push, deploy, force, amend, rebase, or open PRs.
- Do **NOT** alter the existing saga's events (`StockReservationRequestedEvent`, `StockReservedEvent`, etc.) — only the aggregate's internals change.

## Done-report

Standard format. Confirm:
- Renamed class + file (`OrderStockReservation` → `StockReservation`).
- Migration generated and applies cleanly to a fresh test DB.
- Existing Catalog.Integration suite still green (load-bearing — saga callsites switched to `CreateConfirmed`).
- 7 new unit tests in `StockReservationTests.cs` pass.
