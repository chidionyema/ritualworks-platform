using FluentValidation;

namespace Haworks.Identity.Application;

public class GetAvailableProvidersQueryValidator : AbstractValidator<GetAvailableProvidersQuery>
{
    public GetAvailableProvidersQueryValidator()
    {
    }
}

public class VerifyTokenQueryValidator : AbstractValidator<VerifyTokenQuery>
{
    public VerifyTokenQueryValidator()
    {
        RuleFor(x => x.User).NotNull();
    }
}

public class GetUserLoginsQueryValidator : AbstractValidator<GetUserLoginsQuery>
{
    public GetUserLoginsQueryValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
    }
}
