using Haworks.BuildingBlocks.Persistence;

namespace Haworks.Catalog.Domain;

public class OrderStockReservation : AuditableEntity
{
    protected OrderStockReservation() : base() { }

    private OrderStockReservation(Guid id, Guid orderId) : base()
    {
        Id = id;
        OrderId = orderId;
        ReservedAt = DateTime.UtcNow;
    }

    public Guid OrderId { get; private set; }
    public DateTime ReservedAt { get; private set; }
    public DateTime? ReleasedAt { get; private set; }
    public string? ReleaseReason { get; private set; }
    public string ItemsJson { get; private set; } = "[]";

    public static OrderStockReservation Create(Guid orderId, string itemsJson)
    {
        return new OrderStockReservation(Guid.NewGuid(), orderId)
        {
            ItemsJson = itemsJson
        };
    }

    public void MarkReleased(string reason)
    {
        if (ReleasedAt.HasValue) return;
        ReleasedAt = DateTime.UtcNow;
        ReleaseReason = reason;
    }
}
