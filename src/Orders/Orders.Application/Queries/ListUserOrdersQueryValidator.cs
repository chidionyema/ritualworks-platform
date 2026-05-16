using FluentValidation;

namespace Haworks.Orders.Application.Queries;

internal sealed class ListUserOrdersQueryValidator : AbstractValidator<ListUserOrdersQuery>
{
    public ListUserOrdersQueryValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.Skip).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Take).InclusiveBetween(1, 100);
    }
}
