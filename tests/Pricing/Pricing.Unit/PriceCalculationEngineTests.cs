using FluentAssertions;
using Haworks.Pricing.Application.Services;
using Haworks.Pricing.Domain.Entities;
using Haworks.Pricing.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;

namespace Haworks.Pricing.Unit;

[Trait("Category", "PriceEngine")]
public sealed class PriceCalculationEngineTests
{
    private readonly PriceCalculationEngine _engine = new(NullLogger<PriceCalculationEngine>.Instance);
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

    [Fact]
    public void Calculate_NoRules_ReturnsBaseTimesQuantity()
    {
        var productId = Guid.NewGuid();
        var result = _engine.Calculate(productId, 3, 29.99m, "USD", null, Array.Empty<PriceRule>(), null, _now);

        result.BaseUnitPrice.Should().Be(29.99m);
        result.EffectiveUnitPrice.Should().Be(29.99m);
        result.Subtotal.Should().Be(89.97m);
        result.Discounts.Should().BeEmpty();
    }

    [Fact]
    public void Calculate_PercentageDiscount_AppliesCorrectly()
    {
        var productId = Guid.NewGuid();
        var rule = PriceRule.Create(productId, null, 10, DiscountType.Percentage, 10m);

        var result = _engine.Calculate(productId, 1, 100m, "USD", null, new[] { rule }, null, _now);

        result.EffectiveUnitPrice.Should().Be(90m);
        result.Subtotal.Should().Be(90m);
        result.Discounts.Should().HaveCount(1);
        result.Discounts[0].Type.Should().Be("Percentage");
    }

    [Fact]
    public void Calculate_FixedAmountDiscount_AppliesCorrectly()
    {
        var productId = Guid.NewGuid();
        var rule = PriceRule.Create(productId, null, 10, DiscountType.FixedAmount, 5m);

        var result = _engine.Calculate(productId, 2, 20m, "USD", null, new[] { rule }, null, _now);

        result.EffectiveUnitPrice.Should().Be(15m);
        result.Subtotal.Should().Be(30m);
    }

    [Fact]
    public void Calculate_FixedAmountDiscount_FloorsAtZero()
    {
        var productId = Guid.NewGuid();
        var rule = PriceRule.Create(productId, null, 10, DiscountType.FixedAmount, 50m);

        var result = _engine.Calculate(productId, 1, 20m, "USD", null, new[] { rule }, null, _now);

        result.EffectiveUnitPrice.Should().Be(0m);
        result.Subtotal.Should().Be(0m);
    }

    [Fact]
    public void Calculate_TieredPrice_AppliesCorrectTier()
    {
        var productId = Guid.NewGuid();
        var rule = PriceRule.Create(productId, null, 10, DiscountType.Percentage, 5m);
        rule.AddTier(1, 5, 25m);
        rule.AddTier(6, null, 20m);

        var result = _engine.Calculate(productId, 8, 30m, "USD", null, new[] { rule }, null, _now);

        result.EffectiveUnitPrice.Should().Be(20m); // Tier override
        result.Subtotal.Should().Be(160m);
    }

    [Fact]
    public void Calculate_MultipleRules_HigherPriorityFirst()
    {
        var productId = Guid.NewGuid();
        var lowPriority = PriceRule.Create(productId, null, 1, DiscountType.FixedAmount, 2m);
        var highPriority = PriceRule.Create(productId, null, 10, DiscountType.Percentage, 10m);

        var result = _engine.Calculate(productId, 1, 100m, "USD", null, new[] { lowPriority, highPriority }, null, _now);

        // High priority (10% off) first: 100 -> 90, then $2 off: 90 -> 88
        result.EffectiveUnitPrice.Should().Be(88m);
    }

    [Fact]
    public void Calculate_WithPromotionCodePercentage_AppliedToSubtotal()
    {
        var productId = Guid.NewGuid();
        var promo = PromotionCode.Create("WELCOME20", DiscountType.Percentage, 20m);

        var result = _engine.Calculate(productId, 2, 50m, "USD", null, Array.Empty<PriceRule>(), promo, _now);

        // Subtotal = 50 * 2 = 100, then 20% off = 80
        result.Subtotal.Should().Be(80m);
        result.PromoCodeApplied.Should().Be("WELCOME20");
    }

    [Fact]
    public void Calculate_WithPromotionCodeFixed_AppliedToSubtotal()
    {
        var productId = Guid.NewGuid();
        var promo = PromotionCode.Create("SAVE10", DiscountType.FixedAmount, 10m);

        var result = _engine.Calculate(productId, 1, 50m, "USD", null, Array.Empty<PriceRule>(), promo, _now);

        // Subtotal = 50, then $10 off = 40
        result.Subtotal.Should().Be(40m);
    }

    [Fact]
    public void Calculate_PromotionCodeFixed_FloorsAtZero()
    {
        var productId = Guid.NewGuid();
        var promo = PromotionCode.Create("BIGDISCOUNT", DiscountType.FixedAmount, 200m);

        var result = _engine.Calculate(productId, 1, 50m, "USD", null, Array.Empty<PriceRule>(), promo, _now);

        result.Subtotal.Should().Be(0m);
    }

    [Fact]
    public void Calculate_ExpiredPromotion_NotApplied()
    {
        var productId = Guid.NewGuid();
        var promo = PromotionCode.Create(
            "EXPIRED",
            DiscountType.Percentage,
            50m,
            expiresAt: DateTimeOffset.UtcNow.AddDays(-1));

        var result = _engine.Calculate(productId, 1, 100m, "USD", null, Array.Empty<PriceRule>(), promo, _now);

        result.Subtotal.Should().Be(100m);
        result.PromoCodeApplied.Should().BeNull(); // Promo passed but not applied due to CanRedeem=false
    }

    [Fact]
    public void Calculate_UsesDecimalPrecision_NeverFloat()
    {
        var productId = Guid.NewGuid();
        var rule = PriceRule.Create(productId, null, 10, DiscountType.Percentage, 33.33m);

        var result = _engine.Calculate(productId, 3, 10m, "USD", null, new[] { rule }, null, _now);

        // 10 * (1 - 0.3333) = 6.667 rounded to 4dp
        result.EffectiveUnitPrice.Should().Be(6.667m);
        result.Subtotal.Should().Be(20.001m);
    }
}
