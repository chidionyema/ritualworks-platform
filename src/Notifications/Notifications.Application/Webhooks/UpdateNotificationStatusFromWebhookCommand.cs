using MediatR;
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

        await repository.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Success();
    }

    private async Task HandleSesEventAsync(Notification notification, string eventType, string rawPayload, CancellationToken ct)
    {
        switch (eventType)
        {
            case "Delivery":
                notification.MarkDelivered();
                break;
            case "Bounce":
                notification.MarkBounced("SES reported bounce");
                await suppressionService.AddAsync(notification.Recipient, notification.Channel, "SES bounce", null, ct);
                break;
            case "Complaint":
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
                notification.MarkDelivered();
                break;
            case "bounce":
                notification.MarkBounced("SendGrid reported bounce");
                await suppressionService.AddAsync(notification.Recipient, notification.Channel, "SendGrid bounce", null, ct);
                break;
            case "spamreport":
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
                notification.MarkDelivered();
                break;
            case "undelivered":
            case "failed":
                notification.MarkBounced("Twilio reported " + eventType);
                await suppressionService.AddAsync(notification.Recipient, notification.Channel, "Twilio " + eventType, null, ct);
                break;
        }
    }
}
