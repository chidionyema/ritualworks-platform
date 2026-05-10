using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using Haworks.Notifications.Application.Channels;
using Haworks.Notifications.Domain.Entities;
using Haworks.Notifications.Domain.Enums;
using Haworks.Notifications.Infrastructure.Channels.Sms;

namespace Haworks.Notifications.Unit.Channels.Sms;

[Trait("Category", "Unit")]
public sealed class SmsChannelGatewayTests
{
    private const string PrimaryProviderName = "primary";
    private const string SecondaryProviderName = "secondary";
    private const string ProviderMessageId = "provider-msg-id-001";

    [Fact]
    public async Task SendAsync_PrimaryRetryable_SecondarySuccess_MarksSentViaSecondary()
    {
        // Arrange
        var primary = new Mock<ISmsProvider>();
        primary.SetupGet(p => p.Name).Returns(PrimaryProviderName);
        primary
            .Setup(p => p.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ProviderSendResult.Retryable("transient-503"));

        var secondary = new Mock<ISmsProvider>();
        secondary.SetupGet(p => p.Name).Returns(SecondaryProviderName);
        secondary
            .Setup(p => p.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
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
        notification.DeliveryAttempts.First().ProviderName.Should().Be(PrimaryProviderName);
        notification.DeliveryAttempts.Last().IsSuccess.Should().BeTrue();
        notification.DeliveryAttempts.Last().ProviderName.Should().Be(SecondaryProviderName);
        primary.VerifyAll();
        secondary.VerifyAll();
    }

    [Fact]
    public async Task SendAsync_AllProvidersRetryable_MarksFailedAllExhausted()
    {
        // Arrange
        var primary = new Mock<ISmsProvider>();
        primary.SetupGet(p => p.Name).Returns(PrimaryProviderName);
        primary
            .Setup(p => p.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ProviderSendResult.Retryable("transient-1"));

        var secondary = new Mock<ISmsProvider>();
        secondary.SetupGet(p => p.Name).Returns(SecondaryProviderName);
        secondary
            .Setup(p => p.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ProviderSendResult.Retryable("transient-2"));

        var sut = CreateGateway(primary.Object, secondary.Object);
        var notification = NotificationInQueuedState();

        // Act
        await sut.SendAsync(notification, CancellationToken.None);

        // Assert
        notification.Status.Should().Be(NotificationStatus.Failed);
        notification.ErrorMessage.Should().Be("all-providers-exhausted");
        notification.ProviderMessageId.Should().BeNull();
        notification.DeliveryAttempts.Should().HaveCount(2);
        notification.DeliveryAttempts.Should().OnlyContain(a => !a.IsSuccess);
    }

    [Fact]
    public async Task SendAsync_PrimaryNonRetryable_DoesNotTrySecondary()
    {
        // Arrange
        var primary = new Mock<ISmsProvider>();
        primary.SetupGet(p => p.Name).Returns(PrimaryProviderName);
        primary
            .Setup(p => p.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ProviderSendResult.NonRetryable("invalid-recipient"));

        var secondary = new Mock<ISmsProvider>(MockBehavior.Strict);
        secondary.SetupGet(p => p.Name).Returns(SecondaryProviderName);

        var sut = CreateGateway(primary.Object, secondary.Object);
        var notification = NotificationInQueuedState();

        // Act
        await sut.SendAsync(notification, CancellationToken.None);

        // Assert
        notification.Status.Should().Be(NotificationStatus.Failed);
        notification.ErrorMessage.Should().Be("invalid-recipient");
        notification.DeliveryAttempts.Should().HaveCount(1);
        notification.DeliveryAttempts.Single().ProviderName.Should().Be(PrimaryProviderName);
        secondary.Verify(
            p => p.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static SmsChannelGateway CreateGateway(params ISmsProvider[] providers) =>
        new(providers, NullLogger<SmsChannelGateway>.Instance);

    private static Notification NotificationInQueuedState()
    {
        var notification = Notification.Create(
            recipient: "+1234567890",
            channel: NotificationChannel.Sms,
            templateId: "tpl-001",
            idempotencyKey: Guid.NewGuid().ToString("N"),
            subject: "Sms Subject", // domain entity requires it even if SMS doesn't use it
            body: "Sms Body");
        notification.MarkRendering();
        notification.MarkQueued();
        return notification;
    }
}
