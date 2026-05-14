using FluentValidation.TestHelper;
using Haworks.Location.Application.Commands;
using Xunit;

namespace Haworks.Location.Unit.Validators;

public class CreateAddressCommandValidatorTests
{
    private readonly CreateAddressCommandValidator _validator = new();

    private static CreateAddressCommand CreateValidCommand() => new()
    {
        Street = "123 Main St",
        City = "London",
        Postcode = "SW1A 1AA",
        Country = "United Kingdom",
        Latitude = 51.5074,
        Longitude = -0.1278
    };

    [Fact]
    public void Validate_WithValidCommand_ShouldNotHaveErrors()
    {
        var command = CreateValidCommand();
        var result = _validator.TestValidate(command);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Validate_WithInvalidStreet_ShouldHaveError(string? street)
    {
        var command = CreateValidCommand() with { Street = street! };
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.Street);
    }

    [Fact]
    public void Validate_WithStreetTooLong_ShouldHaveError()
    {
        var command = CreateValidCommand() with { Street = new string('a', 501) };
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.Street);
    }

    [Theory]
    [InlineData(-91)]
    [InlineData(91)]
    public void Validate_WithInvalidLatitude_ShouldHaveError(double lat)
    {
        var command = CreateValidCommand() with { Latitude = lat };
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.Latitude);
    }

    [Theory]
    [InlineData(-181)]
    [InlineData(181)]
    public void Validate_WithInvalidLongitude_ShouldHaveError(double lon)
    {
        var command = CreateValidCommand() with { Longitude = lon };
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.Longitude);
    }
}
