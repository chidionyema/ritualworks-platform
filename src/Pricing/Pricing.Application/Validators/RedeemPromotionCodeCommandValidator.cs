using FluentValidation;
using Haworks.Pricing.Application.Commands;

namespace Haworks.Pricing.Application.Validators;

/// <summary>
/// Validates RedeemPromotionCodeCommand input.
/// </summary>
public sealed class RedeemPromotionCodeCommandValidator : AbstractValidator<RedeemPromotionCodeCommand>
{
    public RedeemPromotionCodeCommandValidator()
    {
        RuleFor(x => x.Code)
            .NotEmpty()
            .MaximumLength(32);

        RuleFor(x => x.OrderId)
            .NotEmpty();

        RuleFor(x => x.CalculationId)
            .NotEmpty();

        RuleFor(x => x.DiscountAmount)
            .GreaterThanOrEqualTo(0);
    }
}
