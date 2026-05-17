using FluentAssertions;
using Haworks.Pricing.Domain.Entities;
using Haworks.Pricing.Domain.Enums;

namespace Haworks.Pricing.Unit;

[Trait("Category", "PriceEngine")]
public sealed class PriceRuleTests
{
    [Fact]
    public void Create_WithValidParams_ReturnsRule()
    {
        var rule = PriceRule.Create(
            productId: Guid.NewGuid(),
            categoryId: null,
            priority: 10,
            discountType: DiscountType.Percentage,
            discountValue: 15m);

        rule.Should().NotBeNull();
        rule.IsActive.Should().BeTrue();
        rule.DiscountValue.Should().Be(15m);
    }

    [Fact]
    public void Create_BothProductIdAndCategoryIdNull_Throws()
    {
        var act = () => PriceRule.Create(
            productId: null,
            categoryId: null,
            priority: 1,
            discountType: DiscountType.Percentage,
            discountValue: 10m);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*both be null*");
    }

    [Fact]
    public void Create_DiscountValueZero_Throws()
    {
        var act = () => PriceRule.Create(
            productId: Guid.NewGuid(),
            categoryId: null,
            priority: 1,
            discountType: DiscountType.FixedAmount,
            discountValue: 0m);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*greater than 0*");
    }

    [Fact]
    public void Create_PercentageOver100_Throws()
    {
        var act = () => PriceRule.Create(
            productId: Guid.NewGuid(),
            categoryId: null,
            priority: 1,
            discountType: DiscountType.Percentage,
            discountValue: 101m);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*exceed 100*");
    }

    [Fact]
    public void Create_MaxQuantityLessThanMin_Throws()
    {
        var act = () => PriceRule.Create(
            productId: Guid.NewGuid(),
            categoryId: null,
            priority: 1,
            discountType: DiscountType.FixedAmount,
            discountValue: 5m,
            minimumQuantity: 5,
            maximumQuantity: 3);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*greater than MinimumQuantity*");
    }

    [Fact]
    public void Create_ExpiresBeforeStarts_Throws()
    {
        var now = DateTimeOffset.UtcNow;
        var act = () => PriceRule.Create(
            productId: Guid.NewGuid(),
            categoryId: null,
            priority: 1,
            discountType: DiscountType.Percentage,
            discountValue: 10m,
            startsAt: now.AddDays(2),
            expiresAt: now.AddDays(1));

        act.Should().Throw<ArgumentException>()
            .WithMessage("*after StartsAt*");
    }

    [Fact]
    public void AddTier_OverlappingTiers_Throws()
    {
        var rule = PriceRule.Create(
            productId: Guid.NewGuid(),
            categoryId: null,
            priority: 1,
            discountType: DiscountType.Percentage,
            discountValue: 10m);

        rule.AddTier(1, 10, 25m);

        var act = () => rule.AddTier(5, 15, 20m);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*overlaps*");
    }

    [Fact]
    public void AddTier_NonOverlapping_Succeeds()
    {
        var rule = PriceRule.Create(
            productId: Guid.NewGuid(),
            categoryId: null,
            priority: 1,
            discountType: DiscountType.Percentage,
            discountValue: 10m);

        rule.AddTier(1, 5, 25m);
        rule.AddTier(6, 10, 20m);
        rule.AddTier(11, null, 15m);

        rule.TieredPrices.Should().HaveCount(3);
    }

    [Fact]
    public void Archive_MakesRuleInactive()
    {
        var rule = PriceRule.Create(
            productId: Guid.NewGuid(),
            categoryId: null,
            priority: 1,
            discountType: DiscountType.Percentage,
            discountValue: 10m);

        rule.Archive();

        rule.IsActive.Should().BeFalse();
        rule.IsDeleted.Should().BeTrue();
        rule.Status.Should().Be(PriceRuleStatus.Archived);
    }

    [Fact]
    public void IsApplicableTo_ExpiredRule_ReturnsFalse()
    {
        var productId = Guid.NewGuid();
        var rule = PriceRule.Create(
            productId: productId,
            categoryId: null,
            priority: 1,
            discountType: DiscountType.Percentage,
            discountValue: 10m,
            startsAt: DateTimeOffset.UtcNow.AddDays(-10),
            expiresAt: DateTimeOffset.UtcNow.AddDays(-1));

        var result = rule.IsApplicableTo(productId, null, 1, DateTimeOffset.UtcNow);

        result.Should().BeFalse();
    }

    [Fact]
    public void IsApplicableTo_QuantityBelowMinimum_ReturnsFalse()
    {
        var productId = Guid.NewGuid();
        var rule = PriceRule.Create(
            productId: productId,
            categoryId: null,
            priority: 1,
            discountType: DiscountType.Percentage,
            discountValue: 10m,
            minimumQuantity: 5);

        var result = rule.IsApplicableTo(productId, null, 3, DateTimeOffset.UtcNow);

        result.Should().BeFalse();
    }
}
