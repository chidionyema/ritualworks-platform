using Haworks.Realtime.Api.Application.Common;
using Haworks.Realtime.Api.Application.Notifications;
using Haworks.Realtime.Api.Infrastructure.SignalR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using FluentAssertions;

namespace Haworks.Realtime.Unit.Application.Notifications;

public class SendNotificationCommandHandlerTests
{
    private readonly Mock<IHubContext<NotificationHub>> _hubContextMock;
    private readonly Mock<IInboxService> _inboxServiceMock;
    private readonly Mock<ILogger<SendNotificationCommandHandler>> _loggerMock;
    private readonly SendNotificationCommandHandler _handler;

    public SendNotificationCommandHandlerTests()
    {
        _hubContextMock = new Mock<IHubContext<NotificationHub>>();
        _inboxServiceMock = new Mock<IInboxService>();
        _loggerMock = new Mock<ILogger<SendNotificationCommandHandler>>();

        // Setup HubContext mock
        var clientsMock = new Mock<IHubClients>();
        var clientProxyMock = new Mock<IClientProxy>();
        clientsMock.Setup(x => x.User(It.IsAny<string>())).Returns(clientProxyMock.Object);
        _hubContextMock.Setup(x => x.Clients).Returns(clientsMock.Object);

        _handler = new SendNotificationCommandHandler(
            _hubContextMock.Object,
            _inboxServiceMock.Object,
            _loggerMock.Object);
    }

    [Fact]
    public async Task Handle_Should_StoreMessageInInbox()
    {
        // Arrange
        var command = new SendNotificationCommand
        {
            UserId = Guid.NewGuid(),
            MessageType = "Test",
            Data = new { foo = "bar" }
        };

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        _inboxServiceMock.Verify(x => x.StoreMessageAsync(
            command.UserId,
            It.IsAny<object>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_Should_SendViaSignalR()
    {
        // Arrange
        var command = new SendNotificationCommand
        {
            UserId = Guid.NewGuid(),
            MessageType = "OrderUpdated",
            Data = new { orderId = "123" }
        };

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        var clientsMock = Mock.Get(_hubContextMock.Object.Clients);
        clientsMock.Verify(x => x.User(command.UserId.ToString()), Times.Once);
    }
}
