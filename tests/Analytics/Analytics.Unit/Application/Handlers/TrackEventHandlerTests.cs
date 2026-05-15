using Haworks.Analytics.Api.Application.Commands;
using Haworks.Analytics.Api.Application.Handlers;
using Haworks.Analytics.Api.Infrastructure.Buffer;
using Moq;
using Xunit;
using FluentAssertions;

namespace Haworks.Analytics.Unit.Application.Handlers;

public class TrackEventHandlerTests
{
    private readonly Mock<IEventBuffer> _bufferMock = new();
    private readonly TrackEventHandler _handler;

    public TrackEventHandlerTests()
    {
        _handler = new TrackEventHandler(_bufferMock.Object);
    }

    [Fact]
    public async Task Handle_Should_Enqueue_Event_To_Buffer()
    {
        // Arrange
        var command = new TrackEventCommand("click", "user-1", "session-1", DateTime.UtcNow, null);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        _bufferMock.Verify(x => x.EnqueueAsync(It.Is<ClickstreamEvent>(e => 
            e.EventName == command.EventName &&
            e.UserId == command.UserId &&
            e.OccurredAt == command.OccurredAt), It.IsAny<CancellationToken>()), Times.Once);
    }
}
