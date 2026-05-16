using FluentAssertions;
using FluentValidation.TestHelper;
using Haworks.Media.Api.Application;
using Haworks.Media.Api.Domain;
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

public class MediaFileStateTransitionTests
{
    [Fact]
    public void MarkAsQuarantined_From_Pending_Succeeds()
    {
        var file = MediaFile.Create("test.png", new string('a', 64), 1024, "image/png", "owner");
        file.MarkAsQuarantined();
        file.Status.Should().Be(MediaStatus.Quarantined);
    }

    [Fact]
    public void MarkAsQuarantined_From_Active_Throws()
    {
        var file = MediaFile.Create("test.png", new string('a', 64), 1024, "image/png", "owner");
        file.MarkAsQuarantined();
        file.MarkAsActive();

        var act = () => file.MarkAsQuarantined();
        act.Should().Throw<InvalidOperationException>().WithMessage("*Cannot quarantine from Active*");
    }

    [Fact]
    public void MarkAsActive_From_Pending_Throws()
    {
        var file = MediaFile.Create("test.png", new string('a', 64), 1024, "image/png", "owner");

        var act = () => file.MarkAsActive();
        act.Should().Throw<InvalidOperationException>().WithMessage("*Cannot activate from Pending*");
    }

    [Fact]
    public void MarkAsActive_From_Quarantined_Succeeds()
    {
        var file = MediaFile.Create("test.png", new string('a', 64), 1024, "image/png", "owner");
        file.MarkAsQuarantined();
        file.MarkAsActive();
        file.Status.Should().Be(MediaStatus.Active);
    }

    [Fact]
    public void MarkAsRejected_From_Pending_Throws()
    {
        var file = MediaFile.Create("test.png", new string('a', 64), 1024, "image/png", "owner");

        var act = () => file.MarkAsRejected();
        act.Should().Throw<InvalidOperationException>().WithMessage("*Cannot reject from Pending*");
    }

    [Fact]
    public void MarkAsRejected_From_Quarantined_Succeeds()
    {
        var file = MediaFile.Create("test.png", new string('a', 64), 1024, "image/png", "owner");
        file.MarkAsQuarantined();
        file.MarkAsRejected();
        file.Status.Should().Be(MediaStatus.Rejected);
    }

    [Fact]
    public void MarkAsActive_From_Rejected_Throws()
    {
        var file = MediaFile.Create("test.png", new string('a', 64), 1024, "image/png", "owner");
        file.MarkAsQuarantined();
        file.MarkAsRejected();

        var act = () => file.MarkAsActive();
        act.Should().Throw<InvalidOperationException>().WithMessage("*Cannot activate from Rejected*");
    }
}
