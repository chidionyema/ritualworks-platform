using FluentAssertions;
using Haworks.Notifications.Application.Commands;
using Haworks.Notifications.Application.Suppression;
using Haworks.Notifications.Application.Webhooks;
using Haworks.Notifications.Domain.Entities;
using Haworks.Notifications.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Haworks.Notifications.Unit.Webhooks;

public class UpdateNotificationStatusFromWebhookHandlerTests
{
    private readonly Mock<INotificationRepository> _repositoryMock = new();
    private readonly Mock<ISuppressionService> _suppressionMock = new();
    private readonly UpdateNotificationStatusFromWebhookHandler _sut;

    public UpdateNotificationStatusFromWebhookHandlerTests()
    {
        _sut = new UpdateNotificationStatusFromWebhookHandler(
            _repositoryMock.Object,
            _suppressionMock.Object,
            NullLogger<UpdateNotificationStatusFromWebhookHandler>.Instance);
    }

    [Fact]
    public async Task Handle_SESDelivery_MarksDelivered()
    {
        var notification = CreateSentNotification();
        _repositoryMock.Setup(r => r.GetByProviderMessageIdAsync("msg-123", It.IsAny<CancellationToken>()))
            .ReturnsAsync(notification);

        var command = new UpdateNotificationStatusFromWebhookCommand("SES", "msg-123", "Delivery", "{}");

        await _sut.Handle(command, CancellationToken.None);

        notification.Status.Should().Be(NotificationStatus.Delivered);
        _repositoryMock.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_SESBounce_MarksBouncedAndAddsToSuppression()
    {
        var notification = CreateSentNotification();
        _repositoryMock.Setup(r => r.GetByProviderMessageIdAsync("msg-123", It.IsAny<CancellationToken>()))
            .ReturnsAsync(notification);

        var command = new UpdateNotificationStatusFromWebhookCommand("SES", "msg-123", "Bounce", "{}");

        await _sut.Handle(command, CancellationToken.None);

        notification.Status.Should().Be(NotificationStatus.Bounced);
        _suppressionMock.Verify(s => s.AddAsync(notification.Recipient, notification.Channel, It.IsAny<string>(), null, It.IsAny<CancellationToken>()), Times.Once);
        _repositoryMock.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    private Notification CreateSentNotification()
    {
        var notification = (Notification)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Notification));
        SetPrivate(notification, "Status", NotificationStatus.Sent);
        SetPrivate(notification, "Recipient", "test@example.com");
        SetPrivate(notification, "Channel", NotificationChannel.Email);
        SetPrivate(notification, "ProviderMessageId", "msg-123");
        
        var attemptsField = typeof(Notification).GetField("_deliveryAttempts", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        attemptsField?.SetValue(notification, new List<Haworks.Notifications.Domain.ValueObjects.DeliveryAttempt>());

        return notification;
    }

    private static void SetPrivate(object target, string propertyName, object value)
    {
        var prop = typeof(Notification).GetProperty(propertyName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        prop?.SetValue(target, value);
    }
}
