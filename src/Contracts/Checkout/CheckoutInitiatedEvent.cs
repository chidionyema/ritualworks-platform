namespace Haworks.Contracts.Checkout;

/// <summary>
/// Published when a checkout saga is initiated.
/// This is the first event in the checkout choreography.
/// Consumers:
/// - StockReservationConsumer: Reserves stock for the order items
/// - MetricsConsumer: Records checkout funnel start
/// </summary>
public sealed record CheckoutInitiatedEvent : DomainEvent
{
    /// <summary>The order ID created for this checkout.</summary>
    public required Guid OrderId { get; init; }

    /// <summary>
    /// Unique saga identifier for correlating all events in this checkout flow.
    /// Used for tracing, debugging, and SignalR notifications.
    /// </summary>
    public required Guid SagaId { get; init; }

    /// <summary>The user initiating the checkout.</summary>
    public required string UserId { get; init; }

    /// <summary>The items being purchased.</summary>
    public required IReadOnlyList<CheckoutItemData> Items { get; init; }

    /// <summary>The total order amount including tax.</summary>
    public required decimal TotalAmount { get; init; }

    /// <summary>Customer email for payment session and notifications.</summary>
    public required string CustomerEmail { get; init; }

    /// <summary>ISO 4217 currency code (e.g., "USD", "EUR"). Defaults to USD if not specified.</summary>
    public string? Currency { get; init; }

    /// <summary>Idempotency key to prevent duplicate processing.</summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>Whether this is a guest checkout.</summary>
    public bool IsGuest { get; init; }
}

/// <summary>
/// Represents an item in the checkout for event serialization.
/// </summary>
public sealed record CheckoutItemData
{
    /// <summary>The product being purchased.</summary>
    public required Guid ProductId { get; init; }

    /// <summary>Product name for display purposes.</summary>
    public required string ProductName { get; init; }

    /// <summary>Quantity being purchased.</summary>
    public required int Quantity { get; init; }

    /// <summary>Unit price at time of checkout.</summary>
    public required decimal UnitPrice { get; init; }
}
