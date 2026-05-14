using FluentAssertions;
using Haworks.Scheduler.Application.Scheduling.Commands.ScheduleEvent;
using Xunit;

namespace Haworks.Scheduler.Unit.Application.Validators;

public class ScheduleEventCommandValidatorTests
{
    private readonly ScheduleEventCommandValidator _validator = new();

    [Fact]
    public void Should_Have_Error_When_ScheduledTime_Is_In_Past()
    {
        var command = new ScheduleEventCommand(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            "test-exchange",
            "test.key",
            new { });

        var result = _validator.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(x => x.PropertyName == nameof(ScheduleEventCommand.ScheduledTime));
    }

    [Fact]
    public void Should_Not_Have_Error_When_Command_Is_Valid()
    {
        var command = new ScheduleEventCommand(
            DateTimeOffset.UtcNow.AddDays(1),
            "test-exchange",
            "test.key",
            new { });

        var result = _validator.Validate(command);

        result.IsValid.Should().BeTrue();
    }
}
