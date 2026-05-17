using MediatR;
using Microsoft.EntityFrameworkCore;
using Haworks.BuildingBlocks.Common;
using Haworks.Notifications.Domain.Entities;
using Haworks.Notifications.Domain.Enums;
using Haworks.Notifications.Application.Commands;
using Haworks.Notifications.Application.Suppression;
using Microsoft.Extensions.Logging;

namespace Haworks.Notifications.Application.Webhooks;

public sealed record UpdateNotificationStatusFromWebhookCommand(
    string Provider,
    string ProviderMessageId,
    string EventType,
    string RawPayload) : IRequest<Result>;

internal sealed class UpdateNotificationStatusFromWebhookHandler(
    INotificationRepository repository,
    ISuppressionService suppressionService,
    ILogger<UpdateNotificationStatusFromWebhookHandler> logger
) : IRequestHandler<UpdateNotificationStatusFromWebhookCommand, Result>
{
    public async Task<Result> Handle(UpdateNotificationStatusFromWebhookCommand request, CancellationToken ct)
    {
        var notification = await repository.GetByProviderMessageIdAsync(request.ProviderMessageId, ct).ConfigureAwait(false);
        if (notification == null)
        {
            logger.LogWarning("Webhook received for unknown provider message id: {ProviderMessageId} ({Provider})", 
                request.ProviderMessageId, request.Provider);
            return Result.Success();
        }

        switch (request.Provider)
        {
            case "SES":
                await HandleSesEventAsync(notification, request.EventType, request.RawPayload, ct);
                break;
            case "SendGrid":
                await HandleSendGridEventAsync(notification, request.EventType, request.RawPayload, ct);
                break;
            case "Twilio":
                await HandleTwilioEventAsync(notification, request.EventType, request.RawPayload, ct);
                break;
        }

        // Single SaveChangesAsync commits both the notification status update
        // and any suppression entries atomically (same DbContext/unit of work).
        // Defense-in-depth: if the suppression unique constraint fires (duplicate bounce),
        // the exception propagates but the notification status is preserved.
        try
        {
            await repository.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message?.Contains("23505") == true)
        {
            logger.LogInformation(
                "Duplicate suppression entry for {ProviderMessageId}; saving notification status only",
                request.ProviderMessageId);
            await repository.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return Result.Success();
    }

    /// <summary>
    /// Returns true if the notification is already in the given terminal status,
    /// meaning a webhook replay for the same event should be silently ignored.
    /// </summary>
    private static bool IsAlreadyInStatus(Notification notification, NotificationStatus target) =>
        notification.Status == target;

    private async Task HandleSesEventAsync(Notification notification, string eventType, string rawPayload, CancellationToken ct)
    {
        switch (eventType)
        {
            case "Delivery":
                if (IsAlreadyInStatus(notification, NotificationStatus.Delivered)) break;
                notification.MarkDelivered();
                break;
            case "Bounce":
                if (IsAlreadyInStatus(notification, NotificationStatus.Bounced)) break;
                notification.MarkBounced("SES reported bounce");
                await suppressionService.AddAsync(notification.Recipient, notification.Channel, "SES bounce", null, ct);
                break;
            case "Complaint":
                if (IsAlreadyInStatus(notification, NotificationStatus.Complained)) break;
                notification.MarkComplained();
                await suppressionService.AddAsync(notification.Recipient, notification.Channel, "SES complaint", null, ct);
                break;
        }
    }

    private async Task HandleSendGridEventAsync(Notification notification, string eventType, string rawPayload, CancellationToken ct)
    {
        switch (eventType)
        {
            case "delivered":
                if (IsAlreadyInStatus(notification, NotificationStatus.Delivered)) break;
                notification.MarkDelivered();
                break;
            case "bounce":
                if (IsAlreadyInStatus(notification, NotificationStatus.Bounced)) break;
                notification.MarkBounced("SendGrid reported bounce");
                await suppressionService.AddAsync(notification.Recipient, notification.Channel, "SendGrid bounce", null, ct);
                break;
            case "spamreport":
                if (IsAlreadyInStatus(notification, NotificationStatus.Complained)) break;
                notification.MarkComplained();
                await suppressionService.AddAsync(notification.Recipient, notification.Channel, "SendGrid spamreport", null, ct);
                break;
        }
    }

    private async Task HandleTwilioEventAsync(Notification notification, string eventType, string rawPayload, CancellationToken ct)
    {
        switch (eventType)
        {
            case "delivered":
                if (IsAlreadyInStatus(notification, NotificationStatus.Delivered)) break;
                notification.MarkDelivered();
                break;
            case "undelivered":
            case "failed":
                if (IsAlreadyInStatus(notification, NotificationStatus.Bounced)) break;
                notification.MarkBounced("Twilio reported " + eventType);
                await suppressionService.AddAsync(notification.Recipient, notification.Channel, "Twilio " + eventType, null, ct);
                break;
        }
    }
}
