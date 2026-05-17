using FluentValidation;
using Haworks.Pricing.Application.Commands;
using Haworks.Pricing.Domain.Enums;

namespace Haworks.Pricing.Application.Validators;

/// <summary>
/// Validates CreatePromotionCodeCommand input.
/// </summary>
public sealed class CreatePromotionCodeCommandValidator : AbstractValidator<CreatePromotionCodeCommand>
{
    public CreatePromotionCodeCommandValidator()
    {
        RuleFor(x => x.Code)
            .NotEmpty()
            .MaximumLength(32)
            .Matches("^[A-Za-z0-9_-]+$")
            .WithMessage("Code must contain only alphanumeric characters, hyphens, and underscores.");

        RuleFor(x => x.DiscountValue)
            .GreaterThan(0)
            .WithMessage("DiscountValue must be greater than 0.");

        RuleFor(x => x.DiscountValue)
            .LessThanOrEqualTo(100)
            .When(x => x.DiscountType == DiscountType.Percentage)
            .WithMessage("Percentage discount cannot exceed 100.");

        RuleFor(x => x.ExpiresAt)
            .GreaterThan(x => x.StartsAt)
            .When(x => x.StartsAt.HasValue && x.ExpiresAt.HasValue)
            .WithMessage("ExpiresAt must be after StartsAt.");

        RuleFor(x => x.MaxUses)
            .GreaterThan(0)
            .When(x => x.MaxUses.HasValue);

        RuleFor(x => x.MaxUsesPerUser)
            .GreaterThan(0)
            .When(x => x.MaxUsesPerUser.HasValue);
    }
}
