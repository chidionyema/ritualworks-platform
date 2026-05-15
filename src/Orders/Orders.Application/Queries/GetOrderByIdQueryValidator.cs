using FluentValidation;

namespace Haworks.Orders.Application.Queries;

public class GetOrderByIdQueryValidator : AbstractValidator<GetOrderByIdQuery>
{
    public GetOrderByIdQueryValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
    }
}
