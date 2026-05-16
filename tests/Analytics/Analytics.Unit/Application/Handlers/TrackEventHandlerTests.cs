using Haworks.Analytics.Api.Application.Commands;
using Haworks.Analytics.Api.Application.Handlers;
using Haworks.Analytics.Api.Domain;
using Haworks.Analytics.Api.Infrastructure.Buffer;
using Moq;
using Xunit;
using FluentAssertions;

namespace Haworks.Analytics.Unit.Application.Handlers;

public class TrackEventHandlerTests
{
    private readonly Mock<IEventBuffer> _bufferMock = new();
    private readonly TrackEventCommandHandler _handler;

    public TrackEventHandlerTests()
    {
        _bufferMock.Setup(x => x.NextSequenceNumber()).Returns(1);
        _handler = new TrackEventCommandHandler(_bufferMock.Object);
    }

    [Fact]
    public async Task Handle_Should_Enqueue_Event_To_Buffer()
    {
        var eventId = Guid.NewGuid();
        var command = new TrackEventCommand(eventId, "click", Guid.NewGuid(), "session-1", DateTime.UtcNow, null);
        _bufferMock.Setup(x => x.ContainsEventId(eventId)).Returns(false);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _bufferMock.Verify(x => x.EnqueueAsync(It.Is<ClickstreamEvent>(e =>
            e.EventId == eventId &&
            e.EventName == command.EventName &&
            e.UserId == command.UserId &&
            e.OccurredAt == command.OccurredAt &&
            e.SequenceNumber == 1), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_Should_Deduplicate_By_EventId()
    {
        var eventId = Guid.NewGuid();
        var command = new TrackEventCommand(eventId, "click", Guid.NewGuid(), "session-1", DateTime.UtcNow, null);
        _bufferMock.Setup(x => x.ContainsEventId(eventId)).Returns(true);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _bufferMock.Verify(x => x.EnqueueAsync(It.IsAny<ClickstreamEvent>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_Should_Set_IngestedAt_Timestamp()
    {
        var eventId = Guid.NewGuid();
        var command = new TrackEventCommand(eventId, "click", Guid.NewGuid(), "session-1", DateTime.UtcNow.AddMinutes(-5), null);
        _bufferMock.Setup(x => x.ContainsEventId(eventId)).Returns(false);

        var before = DateTime.UtcNow;
        await _handler.Handle(command, CancellationToken.None);
        var after = DateTime.UtcNow;

        _bufferMock.Verify(x => x.EnqueueAsync(It.Is<ClickstreamEvent>(e =>
            e.IngestedAt >= before && e.IngestedAt <= after), It.IsAny<CancellationToken>()), Times.Once);
    }
}
