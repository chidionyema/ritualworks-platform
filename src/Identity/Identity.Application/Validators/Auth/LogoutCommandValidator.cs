using FluentValidation;

namespace Haworks.Identity.Application.Validators.Auth;

internal sealed class LogoutCommandValidator : AbstractValidator<LogoutCommand>
{
    public LogoutCommandValidator()
    {
        RuleFor(x => x.User).NotNull();
        RuleFor(x => x.HttpContext).NotNull();
    }
}
