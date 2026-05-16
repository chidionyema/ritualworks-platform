using FluentValidation;

namespace Haworks.Identity.Application.Validators.Auth;

internal sealed class UnlinkExternalLoginCommandValidator : AbstractValidator<UnlinkExternalLoginCommand>
{
    public UnlinkExternalLoginCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.Provider).NotEmpty().MaximumLength(100);
    }
}
