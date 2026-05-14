using FluentValidation;

namespace Haworks.Payments.Application.Commands.Subscriptions;

public sealed class CancelSubscriptionCommandValidator : AbstractValidator<CancelSubscriptionCommand>
{
    public CancelSubscriptionCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.SubscriptionId).NotEmpty();
    }
}
