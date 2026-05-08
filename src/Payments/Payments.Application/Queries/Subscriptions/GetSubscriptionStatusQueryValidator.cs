using FluentValidation;

namespace Haworks.Payments.Application.Queries.Subscriptions;

public sealed class GetSubscriptionStatusQueryValidator : AbstractValidator<GetSubscriptionStatusQuery>
{
    public GetSubscriptionStatusQueryValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
    }
}
