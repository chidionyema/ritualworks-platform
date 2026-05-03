namespace Haworks.Contracts.Orders;

/// <summary>
/// Published when a new order is created.
/// Consumers can use this to:
/// - Send order confirmation emails
/// - Update analytics/reporting
/// - Notify inventory systems
/// - Trigger fulfillment workflows
/// </summary>
public sealed record OrderCreatedEvent : DomainEvent
{
    /// <summary>The unique identifier of the created order.</summary>
    public required Guid OrderId { get; init; }

    /// <summary>The customer who placed the order.</summary>
    public required Guid CustomerId { get; init; }

    /// <summary>The total amount of the order.</summary>
    public required decimal TotalAmount { get; init; }

    /// <summary>The customer's email for notifications.</summary>
    public required string CustomerEmail { get; init; }
}
