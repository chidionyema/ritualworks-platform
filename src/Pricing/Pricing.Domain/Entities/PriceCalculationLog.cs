using Haworks.BuildingBlocks.Persistence;

namespace Haworks.Pricing.Domain.Entities;

/// <summary>
/// Append-only audit log for every price calculation. Never updated. Retained for 2 years.
/// </summary>
public sealed class PriceCalculationLog : AuditableEntity
{
    private PriceCalculationLog() { }

    public Guid ProductId { get; private set; }
    public int Quantity { get; private set; }
    public decimal BaseUnitPrice { get; private set; }
    public decimal EffectiveUnitPrice { get; private set; }
    public decimal Subtotal { get; private set; }
    public decimal TaxAmount { get; private set; }
    public decimal TaxRateApplied { get; private set; }
    public decimal Total { get; private set; }
    public string Currency { get; private set; } = "USD";
    public string AppliedRuleIds { get; private set; } = "[]";
    public string? PromotionCodeApplied { get; private set; }
    public DateTimeOffset CalculatedAt { get; private set; }
    public string? UserId { get; private set; }
    public string? CountryCode { get; private set; }
    public string? StateCode { get; private set; }
    public decimal SnapshotProductPrice { get; private set; }

    public static PriceCalculationLog Create(
        Guid productId,
        int quantity,
        decimal baseUnitPrice,
        decimal effectiveUnitPrice,
        decimal subtotal,
        decimal taxAmount,
        decimal taxRateApplied,
        decimal total,
        string currency,
        string appliedRuleIds,
        string? promotionCodeApplied,
        string? userId,
        string? countryCode,
        string? stateCode,
        decimal snapshotProductPrice)
    {
        return new PriceCalculationLog
        {
            ProductId = productId,
            Quantity = quantity,
            BaseUnitPrice = baseUnitPrice,
            EffectiveUnitPrice = effectiveUnitPrice,
            Subtotal = subtotal,
            TaxAmount = taxAmount,
            TaxRateApplied = taxRateApplied,
            Total = total,
            Currency = currency,
            AppliedRuleIds = appliedRuleIds,
            PromotionCodeApplied = promotionCodeApplied,
            CalculatedAt = DateTimeOffset.UtcNow,
            UserId = userId,
            CountryCode = countryCode,
            StateCode = stateCode,
            SnapshotProductPrice = snapshotProductPrice,
        };
    }
}
