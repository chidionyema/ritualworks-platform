namespace Haworks.Contracts.Payments;

/// <summary>
/// Line item data for payment session creation.
/// </summary>
public sealed record PaymentLineItemData
{
    /// <summary>Product name.</summary>
    public required string Name { get; init; }

    /// <summary>Product description.</summary>
    public string? Description { get; init; }

    /// <summary>Unit price in cents.</summary>
    public required long UnitAmountCents { get; init; }

    /// <summary>Quantity.</summary>
    public required int Quantity { get; init; }
}
