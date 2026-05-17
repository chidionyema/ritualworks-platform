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
