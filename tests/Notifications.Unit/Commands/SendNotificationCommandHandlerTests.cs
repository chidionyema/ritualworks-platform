using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using Haworks.Notifications.Application.Commands;
using Haworks.Notifications.Application.Common.Idempotency;
using Haworks.Notifications.Application.Preferences;
using Haworks.Notifications.Application.Suppression;
using Haworks.Notifications.Domain.Entities;
using Haworks.Notifications.Domain.Enums;
using Haworks.BuildingBlocks.Messaging;

namespace Haworks.Notifications.Unit.Commands;

[Trait("Category", "Unit")]
public sealed class SendNotificationCommandHandlerTests
{
    private const string DeterministicKey = "deterministic-key";

    private readonly Mock<INotificationRepository> _repository = new();
    private readonly Mock<IIdempotencyKeyGenerator> _idempotencyKeyGenerator = new();
    private readonly Mock<IPreferencesService> _preferencesService = new();
    private readonly Mock<ISuppressionService> _suppressionService = new();
    private readonly Mock<IDomainEventPublisher> _eventPublisher = new();
    private readonly SendNotificationCommandHandler _sut;

    public SendNotificationCommandHandlerTests()
    {
        _idempotencyKeyGenerator
            .Setup(g => g.Generate(It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(DeterministicKey);

        _sut = new SendNotificationCommandHandler(
            _repository.Object,
            _idempotencyKeyGenerator.Object,
            _preferencesService.Object,
            _suppressionService.Object,
            _eventPublisher.Object,
            NullLogger<SendNotificationCommandHandler>.Instance);
    }

    [Fact]
    public async Task Handle_HappyPath_PersistsNotificationAndPublishesEventBeforeSave()
    {
        // Arrange — preferences allow, recipient not suppressed, idempotency unseen.
        ArrangeAllowedPreferences();
        ArrangeNotSuppressed();
        ArrangeNoExistingIdempotencyHit();

        // Track event/save ordering: PublishAsync MUST happen BEFORE SaveChangesAsync
        // so the OutboxMessage commits in the same EF txn as the Notification INSERT.
        var publishCalledAt = -1;
        var saveCalledAt = -1;
        var sequence = 0;
        _eventPublisher
            .Setup(p => p.PublishAsync(It.IsAny<NotificationCreatedEvent>(), It.IsAny<CancellationToken>()))
            .Callback(() => publishCalledAt = ++sequence)
            .Returns(Task.CompletedTask);
        _repository
            .Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Callback(() => saveCalledAt = ++sequence)
            .ReturnsAsync(1);

        Notification? added = null;
        _repository.Setup(r => r.Add(It.IsAny<Notification>()))
            .Callback<Notification>(n => added = n);

        // Act
        var result = await _sut.Handle(NewCommand(), CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        added.Should().NotBeNull();
        added!.Status.Should().Be(NotificationStatus.Created);
        added.IdempotencyKey.Should().Be(DeterministicKey);

        publishCalledAt.Should().BeGreaterThan(0);
        saveCalledAt.Should().BeGreaterThan(publishCalledAt, "publish must occur before save (outbox guarantee)");

        _eventPublisher.Verify(p => p.PublishAsync(
            It.Is<NotificationCreatedEvent>(e => e.NotificationId == added.Id && e.IdempotencyKey == DeterministicKey),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_ExistingIdempotencyKey_ReturnsExistingIdWithoutSideEffects()
    {
        // Arrange
        var existingId = Guid.NewGuid();
        _repository
            .Setup(r => r.FindIdByIdempotencyKeyAsync(DeterministicKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingId);

        // Act
        var result = await _sut.Handle(NewCommand(), CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(existingId);

        _preferencesService.Verify(
            p => p.IsAllowedAsync(It.IsAny<string>(), It.IsAny<NotificationChannel>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _suppressionService.Verify(
            s => s.IsSuppressedAsync(It.IsAny<string>(), It.IsAny<NotificationChannel>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _eventPublisher.Verify(
            p => p.PublishAsync(It.IsAny<NotificationCreatedEvent>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _repository.Verify(r => r.Add(It.IsAny<Notification>()), Times.Never);
    }

    [Theory]
    [InlineData(PreferenceCheckResult.Suppressed, NotificationStatus.Suppressed)]
    [InlineData(PreferenceCheckResult.QuietHours, NotificationStatus.Suppressed)]
    [InlineData(PreferenceCheckResult.RateLimited, NotificationStatus.Failed)]
    public async Task Handle_PreferenceBlocks_PersistsBlockedRowAndDoesNotPublishEvent(
        PreferenceCheckResult preferenceResult,
        NotificationStatus expectedStatus)
    {
        // Arrange
        ArrangeNoExistingIdempotencyHit();
        _preferencesService
            .Setup(p => p.IsAllowedAsync(It.IsAny<string>(), It.IsAny<NotificationChannel>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(preferenceResult);

        Notification? added = null;
        _repository.Setup(r => r.Add(It.IsAny<Notification>()))
            .Callback<Notification>(n => added = n);
        _repository.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        // Act
        var result = await _sut.Handle(NewCommand(), CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        added.Should().NotBeNull();
        added!.Status.Should().Be(expectedStatus);

        _suppressionService.Verify(
            s => s.IsSuppressedAsync(It.IsAny<string>(), It.IsAny<NotificationChannel>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _eventPublisher.Verify(
            p => p.PublishAsync(It.IsAny<NotificationCreatedEvent>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_RecipientOnSuppressionList_PersistsSuppressedAndDoesNotPublishEvent()
    {
        // Arrange
        ArrangeNoExistingIdempotencyHit();
        ArrangeAllowedPreferences();
        _suppressionService
            .Setup(s => s.IsSuppressedAsync(It.IsAny<string>(), It.IsAny<NotificationChannel>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        Notification? added = null;
        _repository.Setup(r => r.Add(It.IsAny<Notification>()))
            .Callback<Notification>(n => added = n);
        _repository.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        // Act
        var result = await _sut.Handle(NewCommand(), CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        added.Should().NotBeNull();
        added!.Status.Should().Be(NotificationStatus.Suppressed);
        _eventPublisher.Verify(
            p => p.PublishAsync(It.IsAny<NotificationCreatedEvent>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_AnonymousRecipient_SkipsPreferencesGate()
    {
        // Anonymous transactional emails (no UserId) have no preferences to consult.
        // Arrange
        ArrangeNoExistingIdempotencyHit();
        ArrangeNotSuppressed();
        _repository.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        // Act
        var result = await _sut.Handle(NewCommand(userId: null), CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        _preferencesService.Verify(
            p => p.IsAllowedAsync(It.IsAny<string>(), It.IsAny<NotificationChannel>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _eventPublisher.Verify(
            p => p.PublishAsync(It.IsAny<NotificationCreatedEvent>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private void ArrangeAllowedPreferences() =>
        _preferencesService
            .Setup(p => p.IsAllowedAsync(It.IsAny<string>(), It.IsAny<NotificationChannel>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PreferenceCheckResult.Allow);

    private void ArrangeNotSuppressed() =>
        _suppressionService
            .Setup(s => s.IsSuppressedAsync(It.IsAny<string>(), It.IsAny<NotificationChannel>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

    private void ArrangeNoExistingIdempotencyHit() =>
        _repository
            .Setup(r => r.FindIdByIdempotencyKeyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid?)null);

    private static SendNotificationCommand NewCommand(string? userId = "user-1") =>
        new(
            UserId: userId,
            Recipient: "user@example.com",
            Channel: NotificationChannel.Email,
            TemplateId: "welcome",
            Priority: NotificationPriority.Normal,
            Variables: new Dictionary<string, object>(),
            IdempotencyKey: "client-key");
}
