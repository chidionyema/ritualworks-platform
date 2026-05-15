using FluentValidation;

namespace Haworks.Identity.Application.Queries.Users;

public class GetUserProfileQueryValidator : AbstractValidator<GetUserProfileQuery>
{
    public GetUserProfileQueryValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
    }
}
