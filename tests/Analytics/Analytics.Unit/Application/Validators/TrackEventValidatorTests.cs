using FluentValidation.TestHelper;
using Haworks.Analytics.Api.Application.Commands;
using Haworks.Analytics.Api.Application.Validators;
using Xunit;

namespace Haworks.Analytics.Unit.Application.Validators;

public class TrackEventValidatorTests
{
    private readonly TrackEventValidator _validator = new();

    [Fact]
    public void Should_Have_Error_When_EventName_Is_Empty()
    {
        var command = new TrackEventCommand(Guid.NewGuid(), "", Guid.NewGuid(), "session-1", DateTime.UtcNow, null);
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.EventName);
    }

    [Fact]
    public void Should_Have_Error_When_UserId_Is_Empty()
    {
        var command = new TrackEventCommand(Guid.NewGuid(), "click", Guid.Empty, "session-1", DateTime.UtcNow, null);
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.UserId);
    }

    [Fact]
    public void Should_Have_Error_When_EventId_Is_Empty()
    {
        var command = new TrackEventCommand(Guid.Empty, "click", Guid.NewGuid(), "session-1", DateTime.UtcNow, null);
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.EventId);
    }

    [Fact]
    public void Should_Have_Error_When_OccurredAt_Is_More_Than_60_Seconds_In_Future()
    {
        var command = new TrackEventCommand(Guid.NewGuid(), "click", Guid.NewGuid(), "session-1", DateTime.UtcNow.AddSeconds(90), null);
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.OccurredAt);
    }

    [Fact]
    public void Should_Not_Have_Error_When_OccurredAt_Is_Within_60_Seconds_In_Future()
    {
        var command = new TrackEventCommand(Guid.NewGuid(), "click", Guid.NewGuid(), "session-1", DateTime.UtcNow.AddSeconds(30), null);
        var result = _validator.TestValidate(command);
        result.ShouldNotHaveValidationErrorFor(x => x.OccurredAt);
    }

    [Fact]
    public void Should_Not_Have_Error_When_Command_Is_Valid()
    {
        var command = new TrackEventCommand(Guid.NewGuid(), "click", Guid.NewGuid(), "session-1", DateTime.UtcNow, null);
        var result = _validator.TestValidate(command);
        result.ShouldNotHaveAnyValidationErrors();
    }
}
