using FluentValidation;

namespace Haworks.Location.Application.Commands;

/// <summary>
/// Mandatory validator for <see cref="CreateAddressCommand"/>.
/// Wired into the MediatR pipeline via <see cref="Haworks.BuildingBlocks.Behaviors.ValidationBehavior{TRequest, TResponse}"/>.
/// </summary>
public class CreateAddressCommandValidator : AbstractValidator<CreateAddressCommand>
{
    public CreateAddressCommandValidator()
    {
        RuleFor(x => x.Street).NotEmpty().MaximumLength(500);
        RuleFor(x => x.City).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Postcode).NotEmpty().MaximumLength(20);
        RuleFor(x => x.Country).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Latitude).InclusiveBetween(-90, 90);
        RuleFor(x => x.Longitude).InclusiveBetween(-180, 180);
    }
}
