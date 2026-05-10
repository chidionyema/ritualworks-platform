using MassTransit;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Haworks.Notifications.Application.Webhooks;

public sealed class NotificationWebhookValidatedConsumer(
    IMediator mediator,
    ILogger<NotificationWebhookValidatedConsumer> logger
) : IConsumer<NotificationWebhookValidatedEvent>
{
    public async Task Consume(ConsumeContext<NotificationWebhookValidatedEvent> context)
    {
        var evt = context.Message;
        
        logger.LogInformation(
            "Consuming validated webhook: Provider={Provider}, EventType={EventType}, ProviderEventId={ProviderEventId}",
            evt.Provider, evt.EventType, evt.ProviderEventId);

        // Map ProviderEventId to ProviderMessageId if necessary.
        // For SES, the MessageId in SNS IS the ProviderMessageId.
        // For Twilio, the MessageSid IS the ProviderMessageId.
        // For SendGrid, the sg_message_id (or similar) is what we stored.
        
        var command = new UpdateNotificationStatusFromWebhookCommand(
            evt.Provider,
            evt.ProviderEventId,
            evt.EventType,
            evt.RawPayload);

        await mediator.Send(command, context.CancellationToken);
    }
}
