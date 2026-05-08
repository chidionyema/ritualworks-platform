using FluentValidation;

namespace Haworks.Payments.Application.Commands.Subscriptions;

public sealed class CreateSubscriptionCheckoutCommandValidator : AbstractValidator<CreateSubscriptionCheckoutCommand>
{
    public CreateSubscriptionCheckoutCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.PriceId).NotEmpty();
        RuleFor(x => x.Amount).GreaterThan(0);
    }
}
