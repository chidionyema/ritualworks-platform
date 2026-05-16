using FluentValidation;

namespace Haworks.Identity.Application.Validators.Auth;

internal sealed class LinkExternalLoginCommandValidator : AbstractValidator<LinkExternalLoginCommand>
{
    public LinkExternalLoginCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.Provider).NotEmpty().MaximumLength(100);
    }
}
