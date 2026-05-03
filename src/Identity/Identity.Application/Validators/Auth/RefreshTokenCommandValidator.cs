using FluentValidation;
using Haworks.Identity.Application;

namespace Haworks.Identity.Application.Validators.Auth;

internal sealed class RefreshTokenCommandValidator : AbstractValidator<RefreshTokenCommand>
{
    private const int MaxTokenLength = 512;

    public RefreshTokenCommandValidator()
    {
        RuleFor(x => x.RefreshToken)
            .NotEmpty()
            .WithMessage("Refresh token is required")
            .MaximumLength(MaxTokenLength)
            .WithMessage($"Refresh token cannot exceed {MaxTokenLength} characters");
    }
}
