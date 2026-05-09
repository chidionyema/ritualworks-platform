using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using Haworks.Notifications.Application.Channels;
using Haworks.Notifications.Domain.Entities;
using Haworks.Notifications.Domain.Enums;
using Haworks.Notifications.Infrastructure.Channels.Email;

namespace Haworks.Notifications.Unit.Channels.Email;

[Trait("Category", "Unit")]
public sealed class EmailChannelGatewayTests
{
    private const string PrimaryProviderName = "primary";
    private const string SecondaryProviderName = "secondary";
    private const string ProviderMessageId = "provider-msg-id-001";

    [Fact]
    public async Task SendAsync_PrimaryRetryable_SecondarySuccess_MarksSentViaSecondary()
    {
        // Arrange
        var primary = new Mock<IEmailProvider>();
        primary.SetupGet(p => p.Name).Returns(PrimaryProviderName);
        primary
            .Setup(p => p.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ProviderSendResult.Retryable("transient-503"));

        var secondary = new Mock<IEmailProvider>();
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
        var primary = new Mock<IEmailProvider>();
        primary.SetupGet(p => p.Name).Returns(PrimaryProviderName);
        primary
            .Setup(p => p.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ProviderSendResult.Retryable("transient-1"));

        var secondary = new Mock<IEmailProvider>();
        secondary.SetupGet(p => p.Name).Returns(SecondaryProviderName);
        secondary
            .Setup(p => p.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
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
        // Arrange — non-retryable on the primary should short-circuit (the same
        // input would fail on the secondary too: invalid recipient, suppressed,
        // etc). Verifies the gateway honours the provider's "stop" signal.
        var primary = new Mock<IEmailProvider>();
        primary.SetupGet(p => p.Name).Returns(PrimaryProviderName);
        primary
            .Setup(p => p.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ProviderSendResult.NonRetryable("invalid-recipient"));

        var secondary = new Mock<IEmailProvider>(MockBehavior.Strict);
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
            p => p.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "non-retryable failures must not cascade to the next provider");
    }

    private static EmailChannelGateway CreateGateway(params IEmailProvider[] providers) =>
        new(providers, NullLogger<EmailChannelGateway>.Instance);

    /// <summary>Build a Notification and walk it through Created -> Rendering ->
    /// Queued so the gateway's MarkSent / MarkFailed transitions don't trip
    /// the state-machine guard. Uses a fresh GUID idempotency key per call.</summary>
    private static Notification NotificationInQueuedState()
    {
        var notification = Notification.Create(
            recipient: "user@example.com",
            channel: NotificationChannel.Email,
            templateId: "tpl-001",
            idempotencyKey: Guid.NewGuid().ToString("N"),
            subject: "Subject",
            body: "Body");
        notification.MarkRendering();
        notification.MarkQueued();
        return notification;
    }
}
