using FluentValidation;
using Haworks.CheckoutOrchestrator.Application.Queries;

namespace Haworks.CheckoutOrchestrator.Application.Validators;

internal sealed class GetCheckoutSagaQueryValidator : AbstractValidator<GetCheckoutSagaQuery>
{
    public GetCheckoutSagaQueryValidator()
    {
        RuleFor(x => x.SagaId).NotEqual(Guid.Empty);
    }
}

internal sealed class GetCheckoutSagaByOrderIdQueryValidator : AbstractValidator<GetCheckoutSagaByOrderIdQuery>
{
    public GetCheckoutSagaByOrderIdQueryValidator()
    {
        RuleFor(x => x.OrderId).NotEqual(Guid.Empty);
    }
}
