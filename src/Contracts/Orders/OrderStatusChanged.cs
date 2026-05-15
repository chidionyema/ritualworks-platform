namespace Haworks.Contracts.Orders;

public record OrderStatusChanged
{
    public Guid OrderId { get; init; }
    public Guid CustomerId { get; init; }
    public string NewStatus { get; init; } = string.Empty;
    public DateTime ChangedAt { get; init; } = DateTime.UtcNow;
}
