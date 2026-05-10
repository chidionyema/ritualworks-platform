using FluentValidation;
using Haworks.Pricing.Application.Commands;

namespace Haworks.Pricing.Application.Validators;

public sealed class GetPriceQuoteCommandValidator : AbstractValidator<GetPriceQuoteCommand>
{
    public GetPriceQuoteCommandValidator()
    {
        RuleFor(x => x.Lines)
            .NotEmpty().WithMessage("Cart lines cannot be empty.");

        RuleForEach(x => x.Lines).SetValidator(new CartLineDtoValidator());
    }
}

public sealed class CartLineDtoValidator : AbstractValidator<CartLineDto>
{
    public CartLineDtoValidator()
    {
        RuleFor(x => x.ProductId).NotEmpty();
        RuleFor(x => x.Quantity).GreaterThan(0);
        RuleFor(x => x.UnitPrice).GreaterThanOrEqualTo(0);
    }
}
