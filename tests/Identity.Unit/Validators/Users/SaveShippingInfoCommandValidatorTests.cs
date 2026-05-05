using FluentValidation.TestHelper;
using Haworks.Identity.Application.Commands.Users;
using Haworks.Identity.Application.Validators.Users;
using Haworks.Identity.Application.Constants;
using Xunit;

namespace Haworks.Identity.Unit.Validators.Users;

public class SaveShippingInfoCommandValidatorTests
{
    private readonly SaveShippingInfoCommandValidator _validator = new();

    private static SaveShippingInfoCommand CreateValidCommand() => new(
        UserId: Guid.NewGuid().ToString(),
        FirstName: "John",
        LastName: "Doe",
        Address: "123 Main St",
        City: "New York",
        State: "NY",
        PostalCode: "10001",
        Country: "US",
        Phone: null
    );

    [Fact]
    public void Validate_WithValidCommand_ShouldNotHaveErrors()
    {
        var command = CreateValidCommand();
        var result = _validator.TestValidate(command);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_WithEmptyUserId_ShouldHaveError()
    {
        var command = CreateValidCommand() with { UserId = "" };
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.UserId)
            .WithErrorMessage("User ID is required");
    }

    #region Required Fields Validation

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_WithEmptyFirstName_ShouldHaveError(string? firstName)
    {
        var command = CreateValidCommand() with { FirstName = firstName! };
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.FirstName)
            .WithErrorMessage("First name is required");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_WithEmptyLastName_ShouldHaveError(string? lastName)
    {
        var command = CreateValidCommand() with { LastName = lastName! };
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.LastName)
            .WithErrorMessage("Last name is required");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_WithEmptyAddress_ShouldHaveError(string? address)
    {
        var command = CreateValidCommand() with { Address = address! };
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.Address)
            .WithErrorMessage("Address is required");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_WithEmptyCity_ShouldHaveError(string? city)
    {
        var command = CreateValidCommand() with { City = city! };
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.City)
            .WithErrorMessage("City is required");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_WithEmptyPostalCode_ShouldHaveError(string? postalCode)
    {
        var command = CreateValidCommand() with { PostalCode = postalCode! };
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.PostalCode)
            .WithErrorMessage("Postal code is required");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_WithEmptyCountry_ShouldHaveError(string? country)
    {
        var command = CreateValidCommand() with { Country = country! };
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.Country)
            .WithErrorMessage("Country is required");
    }

    #endregion

    #region Max Length Validation

    [Fact]
    public void Validate_WithFirstNameTooLong_ShouldHaveError()
    {
        var command = CreateValidCommand() with { FirstName = new string('a', ValidationConstants.Name.MaxLength + 1) };
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.FirstName)
            .WithErrorMessage($"First name cannot exceed {ValidationConstants.Name.MaxLength} characters");
    }

    [Fact]
    public void Validate_WithLastNameTooLong_ShouldHaveError()
    {
        var command = CreateValidCommand() with { LastName = new string('a', ValidationConstants.Name.MaxLength + 1) };
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.LastName)
            .WithErrorMessage($"Last name cannot exceed {ValidationConstants.Name.MaxLength} characters");
    }

    [Fact]
    public void Validate_WithAddressTooLong_ShouldHaveError()
    {
        var command = CreateValidCommand() with { Address = new string('a', ValidationConstants.Address.MaxStreetLength + 1) };
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.Address)
            .WithErrorMessage($"Address cannot exceed {ValidationConstants.Address.MaxStreetLength} characters");
    }

    [Fact]
    public void Validate_WithCityTooLong_ShouldHaveError()
    {
        var command = CreateValidCommand() with { City = new string('a', ValidationConstants.Address.MaxCityLength + 1) };
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.City)
            .WithErrorMessage($"City cannot exceed {ValidationConstants.Address.MaxCityLength} characters");
    }

    [Fact]
    public void Validate_WithPostalCodeTooLong_ShouldHaveError()
    {
        var command = CreateValidCommand() with { PostalCode = new string('1', ValidationConstants.Address.MaxPostalCodeLength + 1) };
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.PostalCode)
            .WithErrorMessage($"Postal code cannot exceed {ValidationConstants.Address.MaxPostalCodeLength} characters");
    }

    #endregion

    #region Optional State Validation

    [Fact]
    public void Validate_WithStateTooLong_ShouldHaveError()
    {
        var command = CreateValidCommand() with { State = new string('a', ValidationConstants.Address.MaxStateLength + 1) };
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.State)
            .WithErrorMessage($"State cannot exceed {ValidationConstants.Address.MaxStateLength} characters");
    }

    #endregion

    #region Phone Validation

    [Fact]
    public void Validate_WithPhoneTooLong_ShouldHaveError()
    {
        var command = CreateValidCommand() with { Phone = new string('1', ValidationConstants.Phone.MaxLength + 1) };
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.Phone)
            .WithErrorMessage($"Phone cannot exceed {ValidationConstants.Phone.MaxLength} characters");
    }

    [Theory]
    [InlineData("abc123")]
    public void Validate_WithInvalidPhoneChars_ShouldHaveError(string phone)
    {
        var command = CreateValidCommand() with { Phone = phone };
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.Phone)
            .WithErrorMessage("Invalid phone number format");
    }

    #endregion
}
