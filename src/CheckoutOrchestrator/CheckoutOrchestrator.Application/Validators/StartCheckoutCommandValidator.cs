using FluentValidation;
using Haworks.CheckoutOrchestrator.Application.Commands;

namespace Haworks.CheckoutOrchestrator.Application.Validators;

internal sealed class StartCheckoutCommandValidator : AbstractValidator<StartCheckoutCommand>
{
    public StartCheckoutCommandValidator()
    {
        RuleFor(x => x.UserId)
            .NotEmpty()
            .WithMessage("User ID is required");

        RuleFor(x => x.CustomerEmail)
            .NotEmpty()
            .WithMessage("Customer email is required")
            .EmailAddress()
            .WithMessage("Invalid email format");

        RuleFor(x => x.TotalAmount)
            .GreaterThan(0)
            .WithMessage("Total amount must be greater than zero");

        RuleFor(x => x.Items)
            .NotEmpty()
            .WithMessage("Checkout must contain at least one item")
            .Must(x => x.Count <= 100)
            .WithMessage("Checkout cannot exceed 100 items");

        RuleForEach(x => x.Items).ChildRules(item =>
        {
            item.RuleFor(x => x.ProductId).NotEqual(Guid.Empty);
            item.RuleFor(x => x.Quantity).GreaterThan(0).LessThan(100);
            item.RuleFor(x => x.UnitPrice).GreaterThanOrEqualTo(0);
            item.RuleFor(x => x.ProductName).NotEmpty();
        });
    }
}
