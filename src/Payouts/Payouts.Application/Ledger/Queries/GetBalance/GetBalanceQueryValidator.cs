using FluentValidation;

namespace Haworks.Payouts.Application.Ledger.Queries.GetBalance;

internal sealed class GetBalanceQueryValidator : AbstractValidator<GetBalanceQuery>
{
    public GetBalanceQueryValidator()
    {
        RuleFor(x => x.OwnerId).NotEqual(Guid.Empty);
        RuleFor(x => x.Currency).NotEmpty().MaximumLength(3);
        RuleFor(x => x.Type).IsInEnum();
    }
}
