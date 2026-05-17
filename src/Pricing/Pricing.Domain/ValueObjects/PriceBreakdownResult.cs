namespace Haworks.Pricing.Domain.ValueObjects;

/// <summary>
/// Result of a price calculation — the full breakdown returned by the engine.
/// </summary>
public sealed record PriceBreakdownResult
{
    public required Guid CalculationId { get; init; }
    public required Guid ProductId { get; init; }
    public required int Quantity { get; init; }
    public required string Currency { get; init; }
    public required decimal BaseUnitPrice { get; init; }
    public required decimal EffectiveUnitPrice { get; init; }
    public required IReadOnlyList<AppliedDiscount> Discounts { get; init; }
    public required decimal Subtotal { get; init; }
    public required decimal TaxAmount { get; init; }
    public required decimal TaxRate { get; init; }
    public required decimal Total { get; init; }
    public string? PromoCodeApplied { get; init; }
    public required DateTimeOffset SnapshotAt { get; init; }
}

/// <summary>
/// A single discount that was applied during calculation.
/// </summary>
public sealed record AppliedDiscount
{
    public required string Type { get; init; }
    public required string Label { get; init; }
    public required decimal AmountOff { get; init; }
    public decimal? Pct { get; init; }
}
