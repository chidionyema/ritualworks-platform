namespace Haworks.Pricing.Domain.Enums;

/// <summary>
/// The type of discount a pricing rule or promotion code applies.
/// </summary>
public enum DiscountType
{
    /// <summary>Percentage off the unit/subtotal price.</summary>
    Percentage = 0,

    /// <summary>Fixed dollar amount off.</summary>
    FixedAmount = 1,

    /// <summary>Free shipping (not applied to price calculation in v1).</summary>
    FreeShipping = 2,
}
