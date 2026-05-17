namespace Haworks.Contracts.Checkout;

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
