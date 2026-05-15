using FluentValidation.TestHelper;
using Haworks.Media.Api.Application;
using Xunit;

namespace Haworks.Media.Unit;

public class ValidatorTests
{
    private readonly InitiateUploadValidator _initiateValidator = new();
    private readonly ProcessVirusScanValidator _scanValidator = new();

    [Fact]
    public void InitiateUploadValidator_EmptyFileName_ShouldHaveError()
    {
        var command = new InitiateUploadCommand("", new string('a', 64), 1024, "image/png");
        var result = _initiateValidator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.FileName);
    }

    [Fact]
    public void InitiateUploadValidator_InvalidHashLength_ShouldHaveError()
    {
        var command = new InitiateUploadCommand("test.png", "short", 1024, "image/png");
        var result = _initiateValidator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.Hash);
    }

    [Fact]
    public void ProcessVirusScanValidator_EmptyId_ShouldHaveError()
    {
        var command = new ProcessVirusScanCommand(Guid.Empty);
        var result = _scanValidator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.MediaId);
    }
}
