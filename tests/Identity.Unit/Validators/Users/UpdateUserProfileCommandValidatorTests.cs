using FluentValidation.TestHelper;
using Haworks.Identity.Application.Commands.Users;
using Haworks.Identity.Application.Validators.Users;
using Haworks.Identity.Application.Constants;
using Xunit;

namespace Haworks.Identity.Unit.Validators.Users;

public class UpdateUserProfileCommandValidatorTests
{
    private readonly UpdateUserProfileCommandValidator _validator = new();

    private static UpdateUserProfileCommand CreateValidCommand() => new(
        UserId: Guid.NewGuid().ToString(),
        FirstName: "John",
        LastName: "Doe",
        Phone: null,
        Address: null,
        City: null,
        State: null,
        PostalCode: null,
        Country: "US",
        Bio: null,
        Website: null
    );

    [Fact]
    public void Validate_WithValidCommand_ShouldNotHaveErrors()
    {
        var command = CreateValidCommand();
        var result = _validator.TestValidate(command);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_WithAllOptionalFieldsPopulated_ShouldNotHaveErrors()
    {
        var command = new UpdateUserProfileCommand(
            UserId: Guid.NewGuid().ToString(),
            FirstName: "John",
            LastName: "Doe",
            Phone: "+1 555 123 4567",
            Address: "123 Main St",
            City: "New York",
            State: "NY",
            PostalCode: "10001",
            Country: "US",
            Bio: "Software developer",
            Website: "https://example.com"
        );
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

    #region FirstName Validation

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

    [Fact]
    public void Validate_WithFirstNameTooLong_ShouldHaveError()
    {
        var command = CreateValidCommand() with { FirstName = new string('a', ValidationConstants.Name.MaxLength + 1) };
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.FirstName)
            .WithErrorMessage($"First name cannot exceed {ValidationConstants.Name.MaxLength} characters");
    }

    #endregion

    #region LastName Validation

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

    [Fact]
    public void Validate_WithLastNameTooLong_ShouldHaveError()
    {
        var command = CreateValidCommand() with { LastName = new string('a', ValidationConstants.Name.MaxLength + 1) };
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.LastName)
            .WithErrorMessage($"Last name cannot exceed {ValidationConstants.Name.MaxLength} characters");
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
    [InlineData("555@1234")]
    [InlineData("+ (555) 123-4567")] // Space after + not allowed by [\d]
    public void Validate_WithInvalidPhoneChars_ShouldHaveError(string phone)
    {
        var command = CreateValidCommand() with { Phone = phone };
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.Phone)
            .WithErrorMessage("Invalid phone number format");
    }

    [Theory]
    [InlineData("+1-555-123-4567")]
    [InlineData("15551234567")]
    [InlineData("555 123 4567")]
    [InlineData("+44 20 7946 0958")]
    public void Validate_WithValidPhone_ShouldNotHaveError(string phone)
    {
        var command = CreateValidCommand() with { Phone = phone };
        var result = _validator.TestValidate(command);
        result.ShouldNotHaveValidationErrorFor(x => x.Phone);
    }

    #endregion

    #region Bio Validation

    [Fact]
    public void Validate_WithBioTooLong_ShouldHaveError()
    {
        var command = CreateValidCommand() with { Bio = new string('a', 501) };
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.Bio)
            .WithErrorMessage("Bio cannot exceed 500 characters");
    }

    #endregion

    #region Website Validation

    [Fact]
    public void Validate_WithWebsiteTooLong_ShouldHaveError()
    {
        var command = CreateValidCommand() with { Website = new string('a', 101) };
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.Website)
            .WithErrorMessage("Website cannot exceed 100 characters");
    }

    #endregion
}
