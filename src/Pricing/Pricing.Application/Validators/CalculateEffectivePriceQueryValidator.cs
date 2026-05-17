using FluentValidation;
using Haworks.Pricing.Application.Queries;

namespace Haworks.Pricing.Application.Validators;

/// <summary>
/// Validates the CalculateEffectivePriceQuery input.
/// </summary>
public sealed class CalculateEffectivePriceQueryValidator : AbstractValidator<CalculateEffectivePriceQuery>
{
    public CalculateEffectivePriceQueryValidator()
    {
        RuleFor(x => x.ProductId)
            .NotEmpty()
            .WithMessage("ProductId is required.");

        RuleFor(x => x.Quantity)
            .GreaterThan(0)
            .WithMessage("Quantity must be greater than 0.")
            .LessThanOrEqualTo(9999)
            .WithMessage("Quantity cannot exceed 9999.");

        RuleFor(x => x.CountryCode)
            .MaximumLength(2)
            .When(x => x.CountryCode is not null);

        RuleFor(x => x.StateCode)
            .MaximumLength(3)
            .When(x => x.StateCode is not null);

        RuleFor(x => x.PromoCode)
            .MaximumLength(32)
            .When(x => x.PromoCode is not null);
    }
}
