using FluentValidation;
using Haworks.Identity.Application;
using Haworks.Identity.Application.Constants;

namespace Haworks.Identity.Application.Validators.Auth;

internal sealed class LoginCommandValidator : AbstractValidator<LoginCommand>
{
    public LoginCommandValidator()
    {
        RuleFor(x => x.Username)
            .NotEmpty()
            .WithMessage("Username is required")
            .MinimumLength(ValidationConstants.Username.MinLength)
            .WithMessage($"Username must be at least {ValidationConstants.Username.MinLength} characters")
            .MaximumLength(ValidationConstants.Username.MaxLength)
            .WithMessage($"Username cannot exceed {ValidationConstants.Username.MaxLength} characters");

        RuleFor(x => x.Password)
            .NotEmpty()
            .WithMessage("Password is required")
            .MaximumLength(ValidationConstants.Password.MaxLength)
            .WithMessage($"Password cannot exceed {ValidationConstants.Password.MaxLength} characters");
    }
}
