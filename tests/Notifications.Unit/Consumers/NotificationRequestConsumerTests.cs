using FluentAssertions;
using MassTransit;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using Haworks.Notifications.Application.Channels;
using Haworks.Notifications.Application.Commands;
using Haworks.Notifications.Application.Consumers;
using Haworks.Notifications.Application.Templates;
using Haworks.Notifications.Domain.Entities;
using Haworks.Notifications.Domain.Enums;

namespace Haworks.Notifications.Unit.Consumers;

[Trait("Category", "Unit")]
public sealed class NotificationRequestConsumerTests
{
    private const string TemplateId = "tpl-001";

    private readonly Mock<INotificationRepository> _repository = new();
    private readonly Mock<ITemplateSelector> _templateSelector = new();
    private readonly Mock<ITemplateRenderer> _templateRenderer = new();
    private readonly Mock<IEmailChannelGateway> _emailGateway = new();
    private readonly Mock<IPushChannelGateway> _pushGateway = new();
    private readonly NotificationRequestConsumer _sut;

    public NotificationRequestConsumerTests()
    {
        _sut = new NotificationRequestConsumer(
            _repository.Object,
            _templateSelector.Object,
            _templateRenderer.Object,
            _emailGateway.Object,
            _pushGateway.Object,
            NullLogger<NotificationRequestConsumer>.Instance);
    }

    [Fact]
    public async Task Consume_PushChannel_InvokesPushGateway()
    {
        // Arrange
        var notification = CreatedNotification(NotificationChannel.Push);
        ArrangeRepositoryReturns(notification);
        ArrangeTemplateSelectorReturnsStub();
        _pushGateway
            .Setup(g => g.SendAsync(It.IsAny<Notification>(), It.IsAny<CancellationToken>()))
            .Callback<Notification, CancellationToken>((n, _) => n.MarkSent("push-msg-id"))
            .Returns(Task.CompletedTask);

        var ctx = ConsumeContextFor(NewEvent(notification));

        // Act
        await _sut.Consume(ctx.Object);

        // Assert
        notification.Status.Should().Be(NotificationStatus.Sent);
        notification.ProviderMessageId.Should().Be("push-msg-id");
        _pushGateway.Verify(g => g.SendAsync(notification, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Consume_HappyPath_TransitionsThroughRenderingAndQueued_ThenInvokesGateway()
    {
        // Arrange
        var notification = CreatedNotification(NotificationChannel.Email);
        ArrangeRepositoryReturns(notification);
        ArrangeTemplateSelectorReturnsStub();
        _templateRenderer
            .Setup(r => r.RenderAsync(It.IsAny<string>(), It.IsAny<IDictionary<string, object>>()))
            .ReturnsAsync("rendered");
        _emailGateway
            .Setup(g => g.SendAsync(It.IsAny<Notification>(), It.IsAny<CancellationToken>()))
            .Callback<Notification, CancellationToken>((n, _) => n.MarkSent("provider-msg-id"))
            .Returns(Task.CompletedTask);

        var ctx = ConsumeContextFor(NewEvent(notification));

        // Act
        await _sut.Consume(ctx.Object);

        // Assert
        notification.Status.Should().Be(NotificationStatus.Sent);
        notification.ProviderMessageId.Should().Be("provider-msg-id");
        // SaveChanges twice: once after MarkRendering, once after the dispatch.
        _repository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
        _emailGateway.Verify(g => g.SendAsync(notification, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Consume_NotificationNotFound_AcksWithoutCallingGateway()
    {
        // Arrange — outbox normally guarantees the row exists by event consume
        // time, but we ack cleanly on null to avoid poison-loops.
        _repository
            .Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Notification?)null);

        var evt = new NotificationCreatedEvent
        {
            NotificationId = Guid.NewGuid(),
            TemplateId = TemplateId,
            Channel = NotificationChannel.Email,
            Priority = NotificationPriority.Normal,
            Recipient = "user@example.com",
            IdempotencyKey = "idem-key",
        };
        var ctx = ConsumeContextFor(evt);

        // Act
        await _sut.Consume(ctx.Object);

        // Assert
        _emailGateway.Verify(
            g => g.SendAsync(It.IsAny<Notification>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _repository.Verify(
            r => r.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Consume_NotificationAlreadyBeyondCreated_AcksWithoutDispatching()
    {
        // Arrange — re-delivery from MassTransit after the first dispatch
        // already moved the aggregate beyond Created. Short-circuit cleanly.
        var notification = CreatedNotification(NotificationChannel.Email);
        notification.MarkRendering(); // status now Rendering, not Created
        ArrangeRepositoryReturns(notification);
        var ctx = ConsumeContextFor(NewEvent(notification));

        // Act
        await _sut.Consume(ctx.Object);

        // Assert
        _emailGateway.Verify(
            g => g.SendAsync(It.IsAny<Notification>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _repository.Verify(
            r => r.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private void ArrangeRepositoryReturns(Notification notification) =>
        _repository
            .Setup(r => r.GetByIdAsync(notification.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(notification);

    private void ArrangeTemplateSelectorReturnsStub() =>
        _templateSelector
            .Setup(s => s.SelectAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<NotificationChannel>()))
            .ThrowsAsync(new NotImplementedException("L1.B not yet merged"));

    private static Notification CreatedNotification(NotificationChannel channel) =>
        Notification.Create(
            recipient: "user@example.com",
            channel: channel,
            templateId: TemplateId,
            idempotencyKey: Guid.NewGuid().ToString("N"),
            subject: "Subject",
            body: "Body");

    private static NotificationCreatedEvent NewEvent(Notification n) => new()
    {
        NotificationId = n.Id,
        TemplateId = n.TemplateId,
        Channel = n.Channel,
        Priority = n.Priority,
        Recipient = n.Recipient,
        IdempotencyKey = n.IdempotencyKey ?? "idem",
    };

    /// <summary>Minimal MassTransit ConsumeContext mock — we only exercise
    /// Message + CancellationToken so a thin Mock is sufficient (no harness).</summary>
    private static Mock<ConsumeContext<NotificationCreatedEvent>> ConsumeContextFor(
        NotificationCreatedEvent evt)
    {
        var ctx = new Mock<ConsumeContext<NotificationCreatedEvent>>();
        ctx.SetupGet(c => c.Message).Returns(evt);
        ctx.SetupGet(c => c.CancellationToken).Returns(CancellationToken.None);
        return ctx;
    }
}
