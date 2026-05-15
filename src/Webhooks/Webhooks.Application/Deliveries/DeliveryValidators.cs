using FluentValidation;

namespace Haworks.Webhooks.Application.Deliveries;

public class GetDeliveryAttemptsQueryValidator : AbstractValidator<GetDeliveryAttemptsQuery>
{
    public GetDeliveryAttemptsQueryValidator()
    {
        RuleFor(x => x.DeliveryId).NotEmpty();
    }
}

public class ReplayDeliveryCommandValidator : AbstractValidator<ReplayDeliveryCommand>
{
    public ReplayDeliveryCommandValidator()
    {
        RuleFor(x => x.DeliveryId).NotEmpty();
    }
}
