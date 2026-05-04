using FluentValidation.TestHelper;
using Haworks.Identity.Application;
using Haworks.Identity.Application.Validators.Auth;
using Haworks.Identity.Application.Constants;
using Xunit;

namespace Haworks.Identity.Unit.Validators.Auth;

public class LoginCommandValidatorTests
{
    private readonly LoginCommandValidator _validator = new();

    [Fact]
    public void Validate_WithValidRequest_ShouldNotHaveErrors()
    {
        var command = new LoginCommand("testuser", "Password123!", null!);
        var result = _validator.TestValidate(command);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_WithEmptyUsername_ShouldHaveError(string? username)
    {
        var command = new LoginCommand(username!, "Password123!", null!);
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.Username)
            .WithErrorMessage("Username is required");
    }

    [Theory]
    [InlineData("ab")]
    public void Validate_WithUsernameTooShort_ShouldHaveError(string username)
    {
        var command = new LoginCommand(username, "Password123!", null!);
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.Username)
            .WithErrorMessage($"Username must be at least {ValidationConstants.Username.MinLength} characters");
    }

    [Fact]
    public void Validate_WithUsernameTooLong_ShouldHaveError()
    {
        var username = new string('a', ValidationConstants.Username.MaxLength + 1);
        var command = new LoginCommand(username, "Password123!", null!);
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.Username)
            .WithErrorMessage($"Username cannot exceed {ValidationConstants.Username.MaxLength} characters");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_WithEmptyPassword_ShouldHaveError(string? password)
    {
        var command = new LoginCommand("testuser", password!, null!);
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.Password)
            .WithErrorMessage("Password is required");
    }

    [Fact]
    public void Validate_WithPasswordTooLong_ShouldHaveError()
    {
        var password = new string('a', ValidationConstants.Password.MaxLength + 1);
        var command = new LoginCommand("testuser", password, null!);
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.Password)
            .WithErrorMessage($"Password cannot exceed {ValidationConstants.Password.MaxLength} characters");
    }
}
