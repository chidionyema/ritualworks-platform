using Haworks.BuildingBlocks.Persistence;

namespace Haworks.Pricing.Domain.Entities;

/// <summary>
/// Tiered price — owned by PriceRule. Defines an absolute unit price for a quantity range.
/// </summary>
public sealed class TieredPrice : AuditableEntity
{
    private TieredPrice() { }

    public Guid PriceRuleId { get; private set; }
    public int FromQuantity { get; private set; }
    public int? ToQuantity { get; private set; }
    public decimal UnitPrice { get; private set; }

    internal static TieredPrice Create(Guid priceRuleId, int fromQuantity, int? toQuantity, decimal unitPrice)
    {
        return new TieredPrice
        {
            PriceRuleId = priceRuleId,
            FromQuantity = fromQuantity,
            ToQuantity = toQuantity,
            UnitPrice = unitPrice,
        };
    }

    /// <summary>
    /// Checks if a given quantity falls within this tier.
    /// </summary>
    public bool ContainsQuantity(int quantity)
    {
        if (quantity < FromQuantity) return false;
        if (ToQuantity.HasValue && quantity > ToQuantity.Value) return false;
        return true;
    }
}
