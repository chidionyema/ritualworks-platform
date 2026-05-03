namespace Haworks.Contracts.Catalog;

/// <summary>
/// Published when stock reservation fails for an order.
/// This triggers compensation in the checkout saga.
/// Consumers:
/// - OrderStatusConsumer: Updates order status to StockFailed
/// - SignalRNotifier: Notifies user of failure
/// - MetricsConsumer: Records failure for analytics
/// </summary>
public sealed record StockReservationFailedEvent : DomainEvent
{
    /// <summary>The order that failed stock reservation.</summary>
    public required Guid OrderId { get; init; }

    /// <summary>The saga identifier for correlation.</summary>
    public required Guid SagaId { get; init; }

    /// <summary>Items that failed to reserve.</summary>
    public required IReadOnlyList<FailedReservationItem> FailedItems { get; init; }

    /// <summary>Human-readable failure reason.</summary>
    public required string Reason { get; init; }
}

/// <summary>
/// Represents an item that failed stock reservation.
/// </summary>
public sealed record FailedReservationItem
{
    /// <summary>The product that couldn't be reserved.</summary>
    public required Guid ProductId { get; init; }

    /// <summary>Product name for display.</summary>
    public required string ProductName { get; init; }

    /// <summary>Quantity that was requested.</summary>
    public required int RequestedQuantity { get; init; }

    /// <summary>Actual available quantity (if known).</summary>
    public int? AvailableQuantity { get; init; }
}
