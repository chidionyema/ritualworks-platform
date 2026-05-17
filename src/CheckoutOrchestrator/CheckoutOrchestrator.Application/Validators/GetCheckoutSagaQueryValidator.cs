using FluentValidation;

namespace Haworks.CheckoutOrchestrator.Application.Queries;

public sealed class GetCheckoutSagaQueryValidator : AbstractValidator<GetCheckoutSagaQuery>
{
    public GetCheckoutSagaQueryValidator()
    {
        RuleFor(x => x.SagaId).NotEqual(Guid.Empty);
    }
}

public sealed class GetCheckoutSagaByOrderIdQueryValidator : AbstractValidator<GetCheckoutSagaByOrderIdQuery>
{
    public GetCheckoutSagaByOrderIdQueryValidator()
    {
        RuleFor(x => x.OrderId).NotEqual(Guid.Empty);
    }
}
