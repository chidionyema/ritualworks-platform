using FluentValidation;

namespace Haworks.Webhooks.Application.Deliveries;

internal sealed class GetDeliveriesQueryValidator : AbstractValidator<GetDeliveriesQuery>
{
    public GetDeliveriesQueryValidator()
    {
        RuleFor(x => x.CallerId).NotEqual(Guid.Empty);
        RuleFor(x => x.EventType).MaximumLength(200).When(x => x.EventType is not null);
        RuleFor(x => x.Status).MaximumLength(50).When(x => x.Status is not null);
        RuleFor(x => x.Skip).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Take).InclusiveBetween(1, 200);
    }
}
