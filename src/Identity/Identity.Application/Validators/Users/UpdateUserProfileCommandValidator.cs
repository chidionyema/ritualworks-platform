using FluentValidation;
using Haworks.Identity.Application.Commands.Users;
using Haworks.Identity.Application.Constants;

namespace Haworks.Identity.Application.Validators.Users;

internal sealed class UpdateUserProfileCommandValidator : AbstractValidator<UpdateUserProfileCommand>
{
    private const string PhonePattern = @"^\+?[\d][\d\s\-\( )]{0,28}$";

    public UpdateUserProfileCommandValidator()
    {
        RuleFor(x => x.UserId)
            .NotEmpty()
            .WithMessage("User ID is required");

        RuleFor(x => x.FirstName)
            .NotEmpty()
            .WithMessage("First name is required")
            .MaximumLength(ValidationConstants.Name.MaxLength)
            .WithMessage($"First name cannot exceed {ValidationConstants.Name.MaxLength} characters");

        RuleFor(x => x.LastName)
            .NotEmpty()
            .WithMessage("Last name is required")
            .MaximumLength(ValidationConstants.Name.MaxLength)
            .WithMessage($"Last name cannot exceed {ValidationConstants.Name.MaxLength} characters");

        When(x => !string.IsNullOrEmpty(x.Phone), () =>
        {
            RuleFor(x => x.Phone)
                .MaximumLength(ValidationConstants.Phone.MaxLength)
                .WithMessage($"Phone cannot exceed {ValidationConstants.Phone.MaxLength} characters")
                .Matches(PhonePattern)
                .WithMessage("Invalid phone number format");
        });

        RuleFor(x => x.Bio)
            .MaximumLength(500)
            .When(x => x.Bio != null)
            .WithMessage("Bio cannot exceed 500 characters");

        RuleFor(x => x.Website)
            .MaximumLength(100)
            .When(x => x.Website != null)
            .WithMessage("Website cannot exceed 100 characters");
    }
}
