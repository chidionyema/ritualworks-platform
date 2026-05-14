using FluentValidation;

namespace Haworks.Payments.Application.Commands.Subscriptions;

public sealed class ResumeSubscriptionCommandValidator : AbstractValidator<ResumeSubscriptionCommand>
{
    public ResumeSubscriptionCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.SubscriptionId).NotEmpty();
    }
}
