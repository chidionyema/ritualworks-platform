using Haworks.BuildingBlocks.Persistence;

namespace Haworks.Catalog.Domain;

/// <summary>
/// Pre-order stock reservation handle. Lives the lifecycle described in
/// ADR-004:
///
///   Pending  ─Confirm()─►  Confirmed
///       │
///       └─Expire()──►      Expired
///
/// • <see cref="Create"/> — sync flow (B2): created with a TTL while the
///   user is on the checkout page; sweeper (B3) flips stale ones to
///   <see cref="ReservationStatus.Expired"/> and returns stock to the
///   catalog.
/// • <see cref="CreateConfirmed"/> — saga path: jumps straight to
///   <see cref="ReservationStatus.Confirmed"/> so existing
///   StockReservedEvent flows remain observably unchanged.
/// • <see cref="MarkReleased"/> — bookkeeping after the stock has been
///   physically returned (compensation path / sweeper).
/// </summary>
public class StockReservation : AuditableEntity
{
    /// <summary>EF materialization constructor.</summary>
    protected StockReservation() : base() { }

    private StockReservation(
        Guid id,
        string userId,
        string itemsJson,
        DateTime expiresAt,
        ReservationStatus initialStatus) : base()
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
        var r = new StockReservation(
            Guid.NewGuid(),
            userId,
            itemsJson,
            DateTime.UtcNow.AddYears(1),
            ReservationStatus.Confirmed);
        r.OrderId = orderId;
        r.SagaId = sagaId;
        r.ConfirmedAt = DateTime.UtcNow;
        return r;
    }

    public bool Confirm(Guid orderId, Guid sagaId)
    {
        if (Status != ReservationStatus.Pending) return false;
        if (DateTime.UtcNow > ExpiresAt) return false;
        Status = ReservationStatus.Confirmed;
        OrderId = orderId;
        SagaId = sagaId;
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

public enum ReservationStatus
{
    Pending = 0,
    Confirmed = 1,
    Expired = 2,
}
