using FluentValidation;
using Haworks.Identity.Application;
using Haworks.Identity.Application.Constants;

namespace Haworks.Identity.Application.Validators.Auth;

internal sealed class RegisterCommandValidator : AbstractValidator<RegisterCommand>
{
    public RegisterCommandValidator()
    {
        RuleFor(x => x.Username)
            .NotEmpty()
            .WithMessage("Username is required")
            .MinimumLength(ValidationConstants.Username.MinLength)
            .WithMessage($"Username must be at least {ValidationConstants.Username.MinLength} characters")
            .MaximumLength(ValidationConstants.Username.MaxLength)
            .WithMessage($"Username cannot exceed {ValidationConstants.Username.MaxLength} characters")
            .Matches("^[a-zA-Z0-9_-]+$")
            .WithMessage("Username can only contain letters, numbers, underscores, and hyphens");

        RuleFor(x => x.Email)
            .NotEmpty()
            .WithMessage("Email is required")
            .EmailAddress()
            .WithMessage("Invalid email format")
            .MaximumLength(ValidationConstants.Email.MaxLength)
            .WithMessage($"Email cannot exceed {ValidationConstants.Email.MaxLength} characters");

        RuleFor(x => x.Password)
            .NotEmpty()
            .WithMessage("Password is required")
            .MinimumLength(ValidationConstants.Password.MinLength)
            .WithMessage($"Password must be at least {ValidationConstants.Password.MinLength} characters")
            .MaximumLength(ValidationConstants.Password.MaxLength)
            .WithMessage($"Password cannot exceed {ValidationConstants.Password.MaxLength} characters")
            .Matches(ValidationConstants.Password.UppercasePattern)
            .WithMessage("Password must contain at least one uppercase letter")
            .Matches(ValidationConstants.Password.LowercasePattern)
            .WithMessage("Password must contain at least one lowercase letter")
            .Matches(ValidationConstants.Password.DigitPattern)
            .WithMessage("Password must contain at least one digit")
            .Matches(ValidationConstants.Password.SpecialCharPattern)
            .WithMessage("Password must contain at least one special character");
    }
}
