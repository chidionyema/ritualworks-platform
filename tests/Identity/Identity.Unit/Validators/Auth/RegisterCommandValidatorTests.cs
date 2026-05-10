using FluentValidation.TestHelper;
using Haworks.Identity.Application;
using Haworks.Identity.Application.Validators.Auth;
using Haworks.Identity.Application.Constants;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Haworks.Identity.Unit.Validators.Auth;

public class RegisterCommandValidatorTests
{
    private readonly RegisterCommandValidator _validator = new();

    [Fact]
    public void Validate_WithValidRequest_ShouldNotHaveErrors()
    {
        var command = new RegisterCommand("testuser", "test@example.com", "ComplexP@ss123!", null!);
        var result = _validator.TestValidate(command);
        result.ShouldNotHaveAnyValidationErrors();
    }

    #region Username Validation

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_WithEmptyUsername_ShouldHaveError(string? username)
    {
        var command = new RegisterCommand(username!, "test@example.com", "ComplexP@ss123!", null!);
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.Username)
            .WithErrorMessage("Username is required");
    }

    [Theory]
    [InlineData("ab")]
    public void Validate_WithUsernameTooShort_ShouldHaveError(string username)
    {
        var command = new RegisterCommand(username, "test@example.com", "ComplexP@ss123!", null!);
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.Username)
            .WithErrorMessage($"Username must be at least {ValidationConstants.Username.MinLength} characters");
    }

    [Fact]
    public void Validate_WithUsernameTooLong_ShouldHaveError()
    {
        var username = new string('a', ValidationConstants.Username.MaxLength + 1);
        var command = new RegisterCommand(username, "test@example.com", "ComplexP@ss123!", null!);
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.Username)
            .WithErrorMessage($"Username cannot exceed {ValidationConstants.Username.MaxLength} characters");
    }

    [Theory]
    [InlineData("test user")]
    [InlineData("test@user")]
    [InlineData("test.user")]
    public void Validate_WithInvalidUsernameCharacters_ShouldHaveError(string username)
    {
        var command = new RegisterCommand(username, "test@example.com", "ComplexP@ss123!", null!);
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.Username)
            .WithErrorMessage("Username can only contain letters, numbers, underscores, and hyphens");
    }

    [Theory]
    [InlineData("test_user")]
    [InlineData("test-user")]
    [InlineData("TestUser123")]
    public void Validate_WithValidUsernameCharacters_ShouldNotHaveError(string username)
    {
        var command = new RegisterCommand(username, "test@example.com", "ComplexP@ss123!", null!);
        var result = _validator.TestValidate(command);
        result.ShouldNotHaveValidationErrorFor(x => x.Username);
    }

    #endregion

    #region Email Validation

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_WithEmptyEmail_ShouldHaveError(string? email)
    {
        var command = new RegisterCommand("testuser", email!, "ComplexP@ss123!", null!);
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.Email)
            .WithErrorMessage("Email is required");
    }

    [Theory]
    [InlineData("notanemail")]
    [InlineData("@nodomain.com")]
    public void Validate_WithInvalidEmailFormat_ShouldHaveError(string email)
    {
        var command = new RegisterCommand("testuser", email, "ComplexP@ss123!", null!);
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.Email)
            .WithErrorMessage("Invalid email format");
    }

    [Fact]
    public void Validate_WithEmailTooLong_ShouldHaveError()
    {
        var email = new string('a', ValidationConstants.Email.MaxLength + 1) + "@test.com";
        var command = new RegisterCommand("testuser", email, "ComplexP@ss123!", null!);
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.Email)
            .WithErrorMessage($"Email cannot exceed {ValidationConstants.Email.MaxLength} characters");
    }

    #endregion

    #region Password Validation

    [Fact]
    public void Validate_WithValidPassword_ShouldNotHaveError()
    {
        var command = new RegisterCommand("testuser", "test@example.com", "ComplexP@ss123!", null!);
        var result = _validator.TestValidate(command);
        result.ShouldNotHaveValidationErrorFor(x => x.Password);
    }

    [Theory]
    [InlineData("short")] // too short and missing requirements
    public void Validate_WithPasswordTooShort_ShouldHaveError(string password)
    {
        var command = new RegisterCommand("testuser", "test@example.com", password, null!);
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.Password)
            .WithErrorMessage($"Password must be at least {ValidationConstants.Password.MinLength} characters");
    }

    [Theory]
    [InlineData("password123!")] // Missing uppercase
    public void Validate_WithPasswordMissingUppercase_ShouldHaveError(string password)
    {
        var command = new RegisterCommand("testuser", "test@example.com", password, null!);
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.Password)
            .WithErrorMessage("Password must contain at least one uppercase letter");
    }

    [Theory]
    [InlineData("PASSWORD123!")] // Missing lowercase
    public void Validate_WithPasswordMissingLowercase_ShouldHaveError(string password)
    {
        var command = new RegisterCommand("testuser", "test@example.com", password, null!);
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.Password)
            .WithErrorMessage("Password must contain at least one lowercase letter");
    }

    [Theory]
    [InlineData("Password!")] // Missing digit
    public void Validate_WithPasswordMissingDigit_ShouldHaveError(string password)
    {
        var command = new RegisterCommand("testuser", "test@example.com", password, null!);
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.Password)
            .WithErrorMessage("Password must contain at least one digit");
    }

    [Theory]
    [InlineData("Password123")] // Missing special
    public void Validate_WithPasswordMissingSpecialChar_ShouldHaveError(string password)
    {
        var command = new RegisterCommand("testuser", "test@example.com", password, null!);
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.Password)
            .WithErrorMessage("Password must contain at least one special character");
    }

    #endregion
}
