namespace Haworks.Contracts.Catalog;

/// <summary>
/// Published when stock needs to be released back to inventory.
/// This is a compensation event triggered when:
/// - Payment session creation fails
/// - Order is abandoned (timeout)
/// - Order is cancelled by user
/// Consumers:
/// - StockReleaseConsumer: Returns stock to inventory
/// </summary>
public sealed record StockReleaseRequestedEvent : DomainEvent
{
    /// <summary>The order whose stock should be released.</summary>
    public required Guid OrderId { get; init; }

    /// <summary>The saga identifier for correlation.</summary>
    public Guid? SagaId { get; init; }

    /// <summary>Items to release back to inventory.</summary>
    public required IReadOnlyList<StockReservationItem> Items { get; init; }

    /// <summary>
    /// Reason for release. Used for auditing and debugging.
    /// Examples: "payment_session_failed", "order_abandoned", "user_cancelled"
    /// </summary>
    public required string Reason { get; init; }
}
