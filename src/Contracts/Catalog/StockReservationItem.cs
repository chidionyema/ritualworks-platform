namespace Haworks.Contracts.Catalog;

/// <summary>
/// Represents a single product's stock reservation.
/// </summary>
public sealed record StockReservationItem
{
    /// <summary>The product ID.</summary>
    public required Guid ProductId { get; init; }

    /// <summary>The product name (for display purposes).</summary>
    public required string ProductName { get; init; }

    /// <summary>The quantity reserved.</summary>
    public required int Quantity { get; init; }

    /// <summary>The remaining stock after this reservation.</summary>
    public int? RemainingStock { get; init; }
}
