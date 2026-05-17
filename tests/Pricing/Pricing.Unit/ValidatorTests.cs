using FluentAssertions;
using FluentValidation.TestHelper;
using Haworks.Pricing.Application.Commands;
using Haworks.Pricing.Application.Queries;
using Haworks.Pricing.Application.Validators;
using Haworks.Pricing.Domain.Enums;

namespace Haworks.Pricing.Unit;

[Trait("Category", "PriceEngine")]
public sealed class ValidatorTests
{
    [Fact]
    public void CalculateEffectivePriceQuery_EmptyProductId_Fails()
    {
        var validator = new CalculateEffectivePriceQueryValidator();
        var query = new CalculateEffectivePriceQuery
        {
            ProductId = Guid.Empty,
            Quantity = 1,
        };

        var result = validator.TestValidate(query);
        result.ShouldHaveValidationErrorFor(x => x.ProductId);
    }

    [Fact]
    public void CalculateEffectivePriceQuery_QuantityZero_Fails()
    {
        var validator = new CalculateEffectivePriceQueryValidator();
        var query = new CalculateEffectivePriceQuery
        {
            ProductId = Guid.NewGuid(),
            Quantity = 0,
        };

        var result = validator.TestValidate(query);
        result.ShouldHaveValidationErrorFor(x => x.Quantity);
    }

    [Fact]
    public void CalculateEffectivePriceQuery_QuantityOver9999_Fails()
    {
        var validator = new CalculateEffectivePriceQueryValidator();
        var query = new CalculateEffectivePriceQuery
        {
            ProductId = Guid.NewGuid(),
            Quantity = 10000,
        };

        var result = validator.TestValidate(query);
        result.ShouldHaveValidationErrorFor(x => x.Quantity);
    }

    [Fact]
    public void CalculateEffectivePriceQuery_ValidInput_Passes()
    {
        var validator = new CalculateEffectivePriceQueryValidator();
        var query = new CalculateEffectivePriceQuery
        {
            ProductId = Guid.NewGuid(),
            Quantity = 5,
            CountryCode = "US",
            StateCode = "CA",
        };

        var result = validator.TestValidate(query);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void CreatePriceRuleCommand_BothIdsNull_Fails()
    {
        var validator = new CreatePriceRuleCommandValidator();
        var command = new CreatePriceRuleCommand
        {
            ProductId = null,
            CategoryId = null,
            DiscountType = DiscountType.Percentage,
            DiscountValue = 10m,
        };

        var result = validator.TestValidate(command);
        result.ShouldHaveAnyValidationError();
    }

    [Fact]
    public void CreatePromotionCodeCommand_InvalidCode_Fails()
    {
        var validator = new CreatePromotionCodeCommandValidator();
        var command = new CreatePromotionCodeCommand
        {
            Code = "INVALID CODE!",
            DiscountType = DiscountType.Percentage,
            DiscountValue = 10m,
        };

        var result = validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.Code);
    }

    [Fact]
    public void CreatePromotionCodeCommand_ValidInput_Passes()
    {
        var validator = new CreatePromotionCodeCommandValidator();
        var command = new CreatePromotionCodeCommand
        {
            Code = "VALID-CODE_123",
            DiscountType = DiscountType.Percentage,
            DiscountValue = 15m,
            MaxUses = 100,
        };

        var result = validator.TestValidate(command);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void RedeemPromotionCodeCommand_EmptyOrderId_Fails()
    {
        var validator = new RedeemPromotionCodeCommandValidator();
        var command = new RedeemPromotionCodeCommand
        {
            Code = "TEST",
            OrderId = Guid.Empty,
            DiscountAmount = 5m,
            CalculationId = Guid.NewGuid(),
        };

        var result = validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.OrderId);
    }
}
