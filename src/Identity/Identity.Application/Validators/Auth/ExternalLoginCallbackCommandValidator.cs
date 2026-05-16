using FluentValidation;

namespace Haworks.Identity.Application.Validators.Auth;

internal sealed class ExternalLoginCallbackCommandValidator : AbstractValidator<ExternalLoginCallbackCommand>
{
    public ExternalLoginCallbackCommandValidator()
    {
        RuleFor(x => x.HttpContext).NotNull();
    }
}
