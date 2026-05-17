using FluentAssertions;
using Haworks.Pricing.Domain.Entities;
using Haworks.Pricing.Domain.Enums;

namespace Haworks.Pricing.Unit;

[Trait("Category", "PriceEngine")]
public sealed class PromotionCodeTests
{
    [Fact]
    public void Create_ValidCode_Succeeds()
    {
        var code = PromotionCode.Create("WELCOME20", DiscountType.Percentage, 20m, maxUses: 100);

        code.Code.Should().Be("WELCOME20");
        code.DiscountValue.Should().Be(20m);
        code.MaxUses.Should().Be(100);
        code.UsesCount.Should().Be(0);
        code.IsActive.Should().BeTrue();
    }

    [Fact]
    public void Create_CodeStoredUppercase()
    {
        var code = PromotionCode.Create("welcome20", DiscountType.Percentage, 10m);

        code.Code.Should().Be("WELCOME20");
    }

    [Fact]
    public void Create_EmptyCode_Throws()
    {
        var act = () => PromotionCode.Create("", DiscountType.Percentage, 10m);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_CodeTooLong_Throws()
    {
        var act = () => PromotionCode.Create(new string('A', 33), DiscountType.Percentage, 10m);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void CanRedeem_Active_ReturnsTrue()
    {
        var code = PromotionCode.Create("TEST", DiscountType.Percentage, 10m);

        code.CanRedeem(DateTimeOffset.UtcNow).Should().BeTrue();
    }

    [Fact]
    public void CanRedeem_Expired_ReturnsFalse()
    {
        var code = PromotionCode.Create(
            "EXPIRED", DiscountType.Percentage, 10m,
            expiresAt: DateTimeOffset.UtcNow.AddDays(-1));

        code.CanRedeem(DateTimeOffset.UtcNow).Should().BeFalse();
    }

    [Fact]
    public void CanRedeem_NotYetActive_ReturnsFalse()
    {
        var code = PromotionCode.Create(
            "FUTURE", DiscountType.Percentage, 10m,
            startsAt: DateTimeOffset.UtcNow.AddDays(1));

        code.CanRedeem(DateTimeOffset.UtcNow).Should().BeFalse();
    }

    [Fact]
    public void CanRedeem_SoftDeleted_ReturnsFalse()
    {
        var code = PromotionCode.Create("DELETED", DiscountType.Percentage, 10m);
        code.SoftDelete();

        code.CanRedeem(DateTimeOffset.UtcNow).Should().BeFalse();
    }

    [Fact]
    public void IsApplicableTo_MatchingProduct_ReturnsTrue()
    {
        var productId = Guid.NewGuid();
        var code = PromotionCode.Create(
            "PRODUCT", DiscountType.Percentage, 10m,
            applicableProductId: productId);

        code.IsApplicableTo(productId, null).Should().BeTrue();
    }

    [Fact]
    public void IsApplicableTo_DifferentProduct_ReturnsFalse()
    {
        var code = PromotionCode.Create(
            "PRODUCT", DiscountType.Percentage, 10m,
            applicableProductId: Guid.NewGuid());

        code.IsApplicableTo(Guid.NewGuid(), null).Should().BeFalse();
    }

    [Fact]
    public void IsApplicableTo_NoProductRestriction_ReturnsTrue()
    {
        var code = PromotionCode.Create("ALL", DiscountType.Percentage, 10m);

        code.IsApplicableTo(Guid.NewGuid(), null).Should().BeTrue();
    }
}
