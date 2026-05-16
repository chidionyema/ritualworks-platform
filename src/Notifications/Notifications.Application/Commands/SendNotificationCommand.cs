using MediatR;
using Microsoft.Extensions.Logging;
using Haworks.Notifications.Domain.Entities;
using Haworks.Notifications.Domain.Enums;
using Haworks.Notifications.Application.Common.Idempotency;
using Haworks.Notifications.Application.Preferences;
using Haworks.Notifications.Application.Suppression;
using Haworks.BuildingBlocks.Common;
using Haworks.BuildingBlocks.Messaging;

namespace Haworks.Notifications.Application.Commands;

public sealed record SendNotificationCommand(
    string? UserId,
    string Recipient,
    NotificationChannel Channel,
    string TemplateId,
    NotificationPriority Priority,
    IDictionary<string, object> Variables,
    string? IdempotencyKey) : IRequest<Result<Guid>>;

internal sealed class SendNotificationCommandHandler(
    INotificationRepository repository,
    IIdempotencyKeyGenerator idempotencyKeyGenerator,
    IPreferencesService preferencesService,
    ISuppressionService suppressionService,
    IDomainEventPublisher eventPublisher,
    ILogger<SendNotificationCommandHandler> logger
) : IRequestHandler<SendNotificationCommand, Result<Guid>>
{
    // Default category used when callers don't (yet) carry a notification category.
    // L1.B/L1.C will introduce per-template categories; until then preference
    // checks bucket everything as "default" so the gate is meaningful.
    private const string DefaultCategory = "default";

    public async Task<Result<Guid>> Handle(SendNotificationCommand request, CancellationToken ct)
    {
        var idempotencyKey = idempotencyKeyGenerator.Generate(
            request.UserId,
            request.TemplateId,
            request.Recipient,
            request.IdempotencyKey);

        // Idempotency: if we already wrote a Notification for this key, return
        // its Id rather than creating a duplicate. The unique index on
        // IdempotencyKey would otherwise turn a deliberately-retryable command
        // into a 409 at SaveChanges time.
        var existingId = await repository.FindIdByIdempotencyKeyAsync(idempotencyKey, ct).ConfigureAwait(false);
        if (existingId is { } id)
        {
            logger.LogInformation(
                "SendNotificationCommand: returning existing notification {NotificationId} for idempotencyKey {IdempotencyKey}",
                id, idempotencyKey);
            return Result.Success(id);
        }

        // Preferences gate (skip when there's no user — anonymous transactional
        // emails like guest order receipts have no preferences to consult).
        if (!string.IsNullOrWhiteSpace(request.UserId))
        {
            var preferenceResult = await preferencesService
                .IsAllowedAsync(request.UserId, request.Channel, DefaultCategory, ct)
                .ConfigureAwait(false);

            if (preferenceResult != PreferenceCheckResult.Allow)
            {
                var status = preferenceResult switch
                {
                    PreferenceCheckResult.RateLimited => NotificationStatus.Failed,
                    PreferenceCheckResult.QuietHours => NotificationStatus.Suppressed,
                    PreferenceCheckResult.Suppressed => NotificationStatus.Suppressed,
                    _ => NotificationStatus.Suppressed
                };

                var suppressed = CreateNotification(request, idempotencyKey);
                if (status == NotificationStatus.Suppressed)
                    suppressed.MarkSuppressed(preferenceResult.ToString());
                else
                    suppressed.MarkFailed(preferenceResult.ToString());

                repository.Add(suppressed);
                await repository.SaveChangesAsync(ct).ConfigureAwait(false);

                logger.LogInformation(
                    "Notification {NotificationId} blocked by preferences ({Reason}) for user {UserId}",
                    suppressed.Id, preferenceResult, request.UserId);
                return Result.Success(suppressed.Id);
            }
        }

        // Suppression list (bounce/complaint/unsubscribe). Recipient-scoped, so
        // applies regardless of UserId.
        var isSuppressed = await suppressionService
            .IsSuppressedAsync(request.Recipient, request.Channel, ct)
            .ConfigureAwait(false);

        if (isSuppressed)
        {
            var suppressed = CreateNotification(request, idempotencyKey);
            suppressed.MarkSuppressed("Recipient on suppression list");
            repository.Add(suppressed);
            await repository.SaveChangesAsync(ct).ConfigureAwait(false);

            logger.LogInformation(
                "Notification {NotificationId} suppressed for recipient on suppression list",
                suppressed.Id);
            return Result.Success(suppressed.Id);
        }

        // Happy path: create with status Created. Publish event BEFORE SaveChanges
        // so the OutboxMessage commits in the same EF txn as the Notification INSERT.
        var notification = CreateNotification(request, idempotencyKey);
        repository.Add(notification);

        await eventPublisher.PublishAsync(new NotificationCreatedEvent
        {
            NotificationId = notification.Id,
            TemplateId = request.TemplateId,
            Channel = request.Channel,
            Priority = request.Priority,
            UserId = request.UserId,
            Recipient = request.Recipient,
            IdempotencyKey = idempotencyKey
        }, ct).ConfigureAwait(false);

        await repository.SaveChangesAsync(ct).ConfigureAwait(false);
        logger.LogInformation(
            "Notification {NotificationId} created (template {TemplateId}, channel {Channel})",
            notification.Id, request.TemplateId, request.Channel);

        return Result.Success(notification.Id);
    }

    private static Notification CreateNotification(SendNotificationCommand request, string idempotencyKey)
    {
        return Notification.Create(
            request.Recipient,
            request.Channel,
            request.TemplateId,
            idempotencyKey,
            request.UserId,
            request.Priority,
            variables: request.Variables as Dictionary<string, object>
                ?? new Dictionary<string, object>(request.Variables)
        );
    }
}
