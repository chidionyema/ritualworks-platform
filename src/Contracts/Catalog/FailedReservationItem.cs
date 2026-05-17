namespace Haworks.Contracts.Catalog;

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
