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
        var command = new TrackEventCommand("", "user-1", "session-1", DateTime.UtcNow, null);
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.EventName);
    }

    [Fact]
    public void Should_Have_Error_When_UserId_Is_Empty()
    {
        var command = new TrackEventCommand("click", "", "session-1", DateTime.UtcNow, null);
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.UserId);
    }

    [Fact]
    public void Should_Have_Error_When_OccurredAt_Is_Too_Far_In_Future()
    {
        var command = new TrackEventCommand("click", "user-1", "session-1", DateTime.UtcNow.AddHours(1), null);
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.OccurredAt);
    }

    [Fact]
    public void Should_Not_Have_Error_When_Command_Is_Valid()
    {
        var command = new TrackEventCommand("click", "user-1", "session-1", DateTime.UtcNow, null);
        var result = _validator.TestValidate(command);
        result.ShouldNotHaveAnyValidationErrors();
    }
}
