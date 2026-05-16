using FluentValidation;

namespace Haworks.Payouts.Application.Disbursements.Queries.GetPayoutsBySeller;

internal sealed class GetPayoutsBySellerQueryValidator : AbstractValidator<GetPayoutsBySellerQuery>
{
    public GetPayoutsBySellerQueryValidator()
    {
        RuleFor(x => x.SellerId).NotEqual(Guid.Empty);
    }
}
