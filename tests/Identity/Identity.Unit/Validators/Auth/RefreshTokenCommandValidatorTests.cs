using FluentValidation.TestHelper;
using Haworks.Identity.Application;
using Haworks.Identity.Application.Validators.Auth;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Haworks.Identity.Unit.Validators.Auth;

public class RefreshTokenCommandValidatorTests
{
    private readonly RefreshTokenCommandValidator _validator = new();

    [Fact]
    public void Validate_WithValidRequest_ShouldNotHaveErrors()
    {
        var command = new RefreshTokenCommand("valid-access-token", "valid-refresh-token", null!);
        var result = _validator.TestValidate(command);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_WithEmptyRefreshToken_ShouldHaveError(string? refreshToken)
    {
        var command = new RefreshTokenCommand("access-token", refreshToken!, null!);
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.RefreshToken)
            .WithErrorMessage("Refresh token is required");
    }

    [Fact]
    public void Validate_WithTokenTooLong_ShouldHaveError()
    {
        var token = new string('a', 513);
        var command = new RefreshTokenCommand("access-token", token, null!);
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.RefreshToken)
            .WithErrorMessage("Refresh token cannot exceed 512 characters");
    }
}
