using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using Haworks.Notifications.Application.Channels;
using Haworks.Notifications.Domain.Entities;
using Haworks.Notifications.Domain.Enums;
using Haworks.Notifications.Infrastructure.Channels.Push;
using Haworks.Notifications.Domain.ValueObjects;

namespace Haworks.Notifications.Unit.Channels.Push;

[Trait("Category", "Unit")]
public sealed class PushChannelGatewayTests
{
    private const string PrimaryProviderName = "primary";
    private const string SecondaryProviderName = "secondary";
    private const string ProviderMessageId = "provider-msg-id-001";

    [Fact]
    public async Task SendAsync_PrimaryRetryable_SecondarySuccess_MarksSentViaSecondary()
    {
        // Arrange
        var primary = new Mock<IPushProvider>();
        primary.SetupGet(p => p.Name).Returns(PrimaryProviderName);
        primary
            .Setup(p => p.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ProviderSendResult.Retryable("transient-error"));

        var secondary = new Mock<IPushProvider>();
        secondary.SetupGet(p => p.Name).Returns(SecondaryProviderName);
        secondary
            .Setup(p => p.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ProviderSendResult.Success(ProviderMessageId));

        var sut = CreateGateway(primary.Object, secondary.Object);
        var notification = NotificationInQueuedState();

        // Act
        await sut.SendAsync(notification, CancellationToken.None);

        // Assert
        notification.Status.Should().Be(NotificationStatus.Sent);
        notification.ProviderMessageId.Should().Be(ProviderMessageId);
        notification.DeliveryAttempts.Should().HaveCount(2);
        notification.DeliveryAttempts.First().IsSuccess.Should().BeFalse();
        notification.DeliveryAttempts.Last().IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task SendAsync_PrimaryNonRetryable_DoesNotTrySecondary()
    {
        // Arrange
        var primary = new Mock<IPushProvider>();
        primary.SetupGet(p => p.Name).Returns(PrimaryProviderName);
        primary
            .Setup(p => p.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ProviderSendResult.NonRetryable("invalid-token"));

        var secondary = new Mock<IPushProvider>(MockBehavior.Strict);
        secondary.SetupGet(p => p.Name).Returns(SecondaryProviderName);

        var sut = CreateGateway(primary.Object, secondary.Object);
        var notification = NotificationInQueuedState();

        // Act
        await sut.SendAsync(notification, CancellationToken.None);

        // Assert
        notification.Status.Should().Be(NotificationStatus.Failed);
        notification.ErrorMessage.Should().Be("invalid-token");
        notification.DeliveryAttempts.Should().HaveCount(1);
        secondary.Verify(p => p.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SendAsync_AllProvidersExhausted_MarksFailed()
    {
        // Arrange
        var primary = new Mock<IPushProvider>();
        primary.SetupGet(p => p.Name).Returns(PrimaryProviderName);
        primary
            .Setup(p => p.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ProviderSendResult.Retryable("error-1"));

        var sut = CreateGateway(primary.Object);
        var notification = NotificationInQueuedState();

        // Act
        await sut.SendAsync(notification, CancellationToken.None);

        // Assert
        notification.Status.Should().Be(NotificationStatus.Failed);
        notification.ErrorMessage.Should().Be("all-providers-exhausted");
    }

    [Fact]
    public async Task SendAsync_NoProvidersRegistered_MarksFailed()
    {
        // Arrange
        var sut = CreateGateway(); // No providers
        var notification = NotificationInQueuedState();

        // Act
        await sut.SendAsync(notification, CancellationToken.None);

        // Assert
        notification.Status.Should().Be(NotificationStatus.Failed);
        notification.ErrorMessage.Should().Be("no-push-providers-registered");
    }

    [Fact]
    public async Task SendAsync_ProviderThrows_TreatsAsRetryable_FallsThrough()
    {
        // Arrange
        var primary = new Mock<IPushProvider>();
        primary.SetupGet(p => p.Name).Returns(PrimaryProviderName);
        primary
            .Setup(p => p.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("unexpected-boom"));

        var secondary = new Mock<IPushProvider>();
        secondary.SetupGet(p => p.Name).Returns(SecondaryProviderName);
        secondary
            .Setup(p => p.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ProviderSendResult.Success(ProviderMessageId));

        var sut = CreateGateway(primary.Object, secondary.Object);
        var notification = NotificationInQueuedState();

        // Act
        await sut.SendAsync(notification, CancellationToken.None);

        // Assert
        notification.Status.Should().Be(NotificationStatus.Sent);
        notification.DeliveryAttempts.Should().HaveCount(2);
        notification.DeliveryAttempts.First().ErrorMessage.Should().Be("unexpected-boom");
    }

    private static PushChannelGateway CreateGateway(params IPushProvider[] providers) =>
        new(providers, NullLogger<PushChannelGateway>.Instance);

    private static Notification NotificationInQueuedState()
    {
        var notification = Notification.Create(
            recipient: "device-token",
            channel: NotificationChannel.Push,
            templateId: "tpl-001",
            idempotencyKey: Guid.NewGuid().ToString("N"),
            subject: "Subject",
            body: "Body");
        notification.MarkRendering();
        notification.MarkQueued();
        return notification;
    }
}
