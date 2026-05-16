using FluentAssertions;
using Haworks.Scheduler.Application.Scheduling.Commands.ScheduleEvent;
using Xunit;

namespace Haworks.Scheduler.Unit;

public sealed class ScheduleEventCommandValidatorTests
{
    private readonly ScheduleEventCommandValidator _sut = new();

    [Fact]
    public void Valid_command_passes()
    {
        var command = new ScheduleEventCommand(
            DateTimeOffset.UtcNow.AddHours(1),
            "test-exchange",
            "test.routing.key",
            """{"key": "value"}""");

        var result = _sut.Validate(command);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Past_scheduled_time_fails()
    {
        var command = new ScheduleEventCommand(
            DateTimeOffset.UtcNow.AddHours(-1),
            "exchange",
            "key",
            "{}");

        var result = _sut.Validate(command);

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Invalid_json_payload_fails()
    {
        var command = new ScheduleEventCommand(
            DateTimeOffset.UtcNow.AddHours(1),
            "exchange",
            "key",
            "not-json");

        var result = _sut.Validate(command);

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Empty_exchange_fails()
    {
        var command = new ScheduleEventCommand(
            DateTimeOffset.UtcNow.AddHours(1),
            "",
            "key",
            "{}");

        var result = _sut.Validate(command);

        result.IsValid.Should().BeFalse();
    }
}
