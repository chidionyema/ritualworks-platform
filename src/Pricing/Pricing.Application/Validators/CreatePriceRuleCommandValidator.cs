using FluentValidation;
using Haworks.Pricing.Application.Commands;
using Haworks.Pricing.Domain.Enums;

namespace Haworks.Pricing.Application.Validators;

/// <summary>
/// Validates CreatePriceRuleCommand input.
/// </summary>
public sealed class CreatePriceRuleCommandValidator : AbstractValidator<CreatePriceRuleCommand>
{
    public CreatePriceRuleCommandValidator()
    {
        RuleFor(x => x.DiscountValue)
            .GreaterThan(0)
            .WithMessage("DiscountValue must be greater than 0.");

        RuleFor(x => x.DiscountValue)
            .LessThanOrEqualTo(100)
            .When(x => x.DiscountType == DiscountType.Percentage)
            .WithMessage("Percentage discount cannot exceed 100.");

        RuleFor(x => x)
            .Must(x => x.ProductId.HasValue || x.CategoryId.HasValue)
            .WithMessage("ProductId and CategoryId cannot both be null.");

        RuleFor(x => x.MinimumQuantity)
            .GreaterThanOrEqualTo(0);

        RuleFor(x => x.MaximumQuantity)
            .GreaterThan(x => x.MinimumQuantity)
            .When(x => x.MaximumQuantity.HasValue)
            .WithMessage("MaximumQuantity must be greater than MinimumQuantity.");

        RuleFor(x => x.ExpiresAt)
            .GreaterThan(x => x.StartsAt)
            .When(x => x.StartsAt.HasValue && x.ExpiresAt.HasValue)
            .WithMessage("ExpiresAt must be after StartsAt.");
    }
}
