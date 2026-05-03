namespace Haworks.Contracts.Orders;

/// <summary>
/// Published when an order is marked as completed (payment verified, ready for fulfillment).
/// Consumers can use this to:
/// - Trigger fulfillment/shipping workflows
/// - Send order completion confirmation
/// - Update analytics/reporting
/// - Notify warehouse systems
/// </summary>
public sealed record OrderCompletedEvent : DomainEvent
{
    /// <summary>The unique identifier of the completed order.</summary>
    public required Guid OrderId { get; init; }

    /// <summary>The customer who placed the order (null for guest orders).</summary>
    public Guid? CustomerId { get; init; }

    /// <summary>The total amount of the order.</summary>
    public required decimal TotalAmount { get; init; }

    /// <summary>The customer's email for notifications.</summary>
    public required string CustomerEmail { get; init; }

    /// <summary>When the order was completed.</summary>
    public required DateTime CompletedAt { get; init; }

    /// <summary>The payment ID associated with this order.</summary>
    public required Guid PaymentId { get; init; }
}
