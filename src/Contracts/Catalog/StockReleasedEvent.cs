namespace Haworks.Contracts.Catalog;

/// <summary>
/// Published when stock has been successfully released back to inventory.
/// This confirms the compensation was applied.
/// Consumers:
/// - StockCacheConsumer: Updates Redis cache
/// - MetricsConsumer: Records release for analytics
/// - AuditConsumer: Logs the release for compliance
/// </summary>
public sealed record StockReleasedEvent : DomainEvent
{
    /// <summary>The order whose stock was released.</summary>
    public required Guid OrderId { get; init; }

    /// <summary>Items that were released.</summary>
    public required IReadOnlyList<StockReservationItem> Items { get; init; }

    /// <summary>Total units released across all items.</summary>
    public int TotalUnitsReleased => Items.Sum(i => i.Quantity);

    /// <summary>Reason the stock was released.</summary>
    public required string Reason { get; init; }
}
