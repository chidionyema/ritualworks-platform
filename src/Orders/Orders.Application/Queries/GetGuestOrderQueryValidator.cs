using FluentValidation;

namespace Haworks.Orders.Application.Queries;

internal sealed class GetGuestOrderQueryValidator : AbstractValidator<GetGuestOrderQuery>
{
    public GetGuestOrderQueryValidator()
    {
        RuleFor(x => x.Token).NotEmpty().MaximumLength(500);
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(254);
    }
}
