using FluentAssertions;
using Haworks.BuildingBlocks.CurrentUser;
using Haworks.Scheduler.Application.Common.Interfaces;
using Haworks.Scheduler.Application.Scheduling.Commands.ScheduleEvent;
using Moq;
using Xunit;

namespace Haworks.Scheduler.Unit;

public sealed class ScheduleEventCommandHandlerTests
{
    private readonly Mock<IEventScheduler> _schedulerMock = new();
    private readonly Mock<ICurrentUserService> _currentUserMock = new();
    private readonly ScheduleEventCommandHandler _handler;

    public ScheduleEventCommandHandlerTests()
    {
        _currentUserMock.Setup(x => x.UserId).Returns("user-123");
        _handler = new ScheduleEventCommandHandler(_schedulerMock.Object, _currentUserMock.Object);
    }

    [Fact]
    public async Task Handle_passes_idempotency_key_to_scheduler()
    {
        const string idempotencyKey = "key-abc";
        _schedulerMock
            .Setup(x => x.ScheduleEventAsync(
                idempotencyKey,
                It.IsAny<DateTimeOffset>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string?>()))
            .ReturnsAsync("job-1");

        var command = new ScheduleEventCommand(
            idempotencyKey,
            DateTimeOffset.UtcNow.AddHours(1),
            "exchange",
            "key",
            "{}");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.JobId.Should().Be("job-1");
        _schedulerMock.Verify(x => x.ScheduleEventAsync(
            idempotencyKey,
            command.ScheduledTime,
            command.TargetExchange,
            command.RoutingKey,
            command.Payload,
            "user-123"), Times.Once);
    }

    [Fact]
    public async Task Handle_same_idempotency_key_returns_same_job_id()
    {
        const string idempotencyKey = "dedup-key";
        _schedulerMock
            .Setup(x => x.ScheduleEventAsync(
                idempotencyKey,
                It.IsAny<DateTimeOffset>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string?>()))
            .ReturnsAsync("job-dedup");

        var command = new ScheduleEventCommand(
            idempotencyKey,
            DateTimeOffset.UtcNow.AddHours(1),
            "exchange",
            "key",
            "{}");

        var result1 = await _handler.Handle(command, CancellationToken.None);
        var result2 = await _handler.Handle(command, CancellationToken.None);

        result1.JobId.Should().Be("job-dedup");
        result2.JobId.Should().Be("job-dedup");
    }

    [Fact]
    public async Task Handle_uses_system_when_user_is_null()
    {
        _currentUserMock.Setup(x => x.UserId).Returns((string?)null);
        var handler = new ScheduleEventCommandHandler(_schedulerMock.Object, _currentUserMock.Object);

        _schedulerMock
            .Setup(x => x.ScheduleEventAsync(
                It.IsAny<string>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                "system"))
            .ReturnsAsync("job-sys");

        var command = new ScheduleEventCommand(
            "key-sys",
            DateTimeOffset.UtcNow.AddHours(1),
            "exchange",
            "key",
            "{}");

        var result = await handler.Handle(command, CancellationToken.None);

        result.JobId.Should().Be("job-sys");
    }
}
