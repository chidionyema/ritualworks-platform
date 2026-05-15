using FluentValidation;

namespace Haworks.Payments.Application.Queries.Refunds;

public class GetRefundSagaStateQueryValidator : AbstractValidator<GetRefundSagaStateQuery>
{
    public GetRefundSagaStateQueryValidator()
    {
        RuleFor(x => x.RefundId).NotEmpty();
    }
}
