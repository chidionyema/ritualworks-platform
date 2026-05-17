namespace Haworks.Orders.Domain;

/// <summary>
/// Immutable audit record capturing each status transition on an Order.
/// Stored in a dedicated table for compliance and operational forensics.
/// </summary>
public sealed class OrderStatusHistory
{
    public Guid Id { get; private set; }
    public Guid OrderId { get; private set; }
    public OrderStatus FromStatus { get; private set; }
    public OrderStatus ToStatus { get; private set; }
    public DateTimeOffset ChangedAt { get; private set; }
    public string? ChangedBy { get; private set; }
    public string? Reason { get; private set; }

    private OrderStatusHistory() { }

    public static OrderStatusHistory Create(
        Guid orderId,
        OrderStatus from,
        OrderStatus to,
        string? changedBy = null,
        string? reason = null)
    {
        return new OrderStatusHistory
        {
            Id = Guid.NewGuid(),
            OrderId = orderId,
            FromStatus = from,
            ToStatus = to,
            ChangedAt = DateTimeOffset.UtcNow,
            ChangedBy = changedBy,
            Reason = reason
        };
    }
}
