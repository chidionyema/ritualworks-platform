using FluentValidation;
using Haworks.Identity.Application.Commands.Users;
using Haworks.Identity.Application.Constants;

namespace Haworks.Identity.Application.Validators.Users;

internal sealed class SaveShippingInfoCommandValidator : AbstractValidator<SaveShippingInfoCommand>
{
    private const string PhonePattern = @"^\+?[\d][\d\s\-\( )]{0,28}$";

    public SaveShippingInfoCommandValidator()
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

        RuleFor(x => x.Address)
            .NotEmpty()
            .WithMessage("Address is required")
            .MaximumLength(ValidationConstants.Address.MaxStreetLength)
            .WithMessage($"Address cannot exceed {ValidationConstants.Address.MaxStreetLength} characters");

        RuleFor(x => x.City)
            .NotEmpty()
            .WithMessage("City is required")
            .MaximumLength(ValidationConstants.Address.MaxCityLength)
            .WithMessage($"City cannot exceed {ValidationConstants.Address.MaxCityLength} characters");

        RuleFor(x => x.State)
            .MaximumLength(ValidationConstants.Address.MaxStateLength)
            .When(x => x.State != null)
            .WithMessage($"State cannot exceed {ValidationConstants.Address.MaxStateLength} characters");

        RuleFor(x => x.PostalCode)
            .NotEmpty()
            .WithMessage("Postal code is required")
            .MaximumLength(ValidationConstants.Address.MaxPostalCodeLength)
            .WithMessage($"Postal code cannot exceed {ValidationConstants.Address.MaxPostalCodeLength} characters");

        RuleFor(x => x.Country)
            .NotEmpty()
            .WithMessage("Country is required")
            .MaximumLength(ValidationConstants.Address.MaxCountryLength)
            .WithMessage($"Country cannot exceed {ValidationConstants.Address.MaxCountryLength} characters");

        When(x => !string.IsNullOrEmpty(x.Phone), () =>
        {
            RuleFor(x => x.Phone)
                .MaximumLength(ValidationConstants.Phone.MaxLength)
                .WithMessage($"Phone cannot exceed {ValidationConstants.Phone.MaxLength} characters")
                .Matches(PhonePattern)
                .WithMessage("Invalid phone number format");
        });
    }
}
