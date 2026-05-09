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

                var suppressed = HydrateNotification(request, idempotencyKey, status, preferenceResult.ToString());
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
            var suppressed = HydrateNotification(request, idempotencyKey, NotificationStatus.Suppressed, "Recipient on suppression list");
            repository.Add(suppressed);
            await repository.SaveChangesAsync(ct).ConfigureAwait(false);

            logger.LogInformation(
                "Notification {NotificationId} suppressed for recipient on suppression list",
                suppressed.Id);
            return Result.Success(suppressed.Id);
        }

        // Happy path: create with status Created. Publish event BEFORE SaveChanges
        // so the OutboxMessage commits in the same EF txn as the Notification INSERT.
        var notification = HydrateNotification(request, idempotencyKey, NotificationStatus.Created, errorMessage: null);
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

    /// <summary>
    /// Wraps <see cref="Notification.Create"/>. L1.A may still be filling that
    /// factory body; if it throws <see cref="NotImplementedException"/> we
    /// fall back to reflection-based hydration so the L1.G surface compiles
    /// and runs end-to-end today. The TODO disappears when L1.A lands a real
    /// factory that accepts the command parameters.
    /// </summary>
    private static Notification HydrateNotification(
        SendNotificationCommand request,
        string idempotencyKey,
        NotificationStatus status,
        string? errorMessage)
    {
        Notification notification;
        try
        {
            // Preferred path once L1.A lands a real factory.
            notification = Notification.Create();
            // TODO(notif-L1.G): once L1.A's Notification.Create accepts the
            // command parameters, drop the reflection fallback below and
            // remove this hydration helper entirely.
        }
        catch (NotImplementedException)
        {
            // TODO(notif-L1.G): wait for L1.A. Until then we materialise the
            // entity through its private setters via reflection so the row
            // reflects the command intent end-to-end (idempotency key, status,
            // recipient, etc).
            notification = (Notification)System.Runtime.CompilerServices.RuntimeHelpers
                .GetUninitializedObject(typeof(Notification));

            // AuditableEntity defaults — set via the public setters on the base type.
            var baseType = typeof(Notification).BaseType!;
            baseType.GetProperty("Id")!.SetValue(notification, Guid.NewGuid());
            baseType.GetProperty("CreatedAt")!.SetValue(notification, DateTime.UtcNow);
            baseType.GetProperty("RowVersion")!.SetValue(notification, new byte[8]);

            // Initialise the backing list so EF Core OwnsMany doesn't NRE.
            var attemptsField = typeof(Notification).GetField(
                "_deliveryAttempts",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            attemptsField?.SetValue(notification, new List<Domain.ValueObjects.DeliveryAttempt>());
        }

        SetPrivate(notification, nameof(Notification.UserId), request.UserId);
        SetPrivate(notification, nameof(Notification.Recipient), request.Recipient);
        SetPrivate(notification, nameof(Notification.Channel), request.Channel);
        SetPrivate(notification, nameof(Notification.TemplateId), request.TemplateId);
        SetPrivate(notification, nameof(Notification.Priority), request.Priority);
        SetPrivate(notification, nameof(Notification.Status), status);
        SetPrivate(notification, nameof(Notification.Subject), string.Empty);
        SetPrivate(notification, nameof(Notification.Body), string.Empty);
        SetPrivate(notification, nameof(Notification.ErrorMessage), errorMessage);
        SetPrivate(notification, nameof(Notification.IdempotencyKey), idempotencyKey);

        return notification;
    }

    private static void SetPrivate(object target, string propertyName, object? value)
    {
        var prop = typeof(Notification).GetProperty(
            propertyName,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        prop?.SetValue(target, value);
    }
}
