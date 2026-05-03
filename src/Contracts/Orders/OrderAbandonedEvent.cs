namespace Haworks.Contracts.Orders;

/// <summary>
/// Published when an order is abandoned (timeout without payment).
/// Triggers stock release compensation.
/// Consumers:
/// - StockReleaseConsumer: Returns reserved stock to inventory
/// - MetricsConsumer: Records abandonment for analytics
/// - RecoveryConsumer: May trigger cart recovery email
/// </summary>
public sealed record OrderAbandonedEvent : DomainEvent
{
    /// <summary>The abandoned order ID.</summary>
    public required Guid OrderId { get; init; }

    /// <summary>The saga identifier for correlation.</summary>
    public Guid? SagaId { get; init; }

    /// <summary>Items to release back to inventory.</summary>
    public required IReadOnlyList<Catalog.StockReservationItem> Items { get; init; }

    /// <summary>How long the order was pending before abandonment.</summary>
    public required TimeSpan AgeAtAbandonment { get; init; }

    /// <summary>The previous status before being marked abandoned.</summary>
    public required string PreviousStatus { get; init; }

    /// <summary>Customer email for potential recovery campaign.</summary>
    public string? CustomerEmail { get; init; }
}
