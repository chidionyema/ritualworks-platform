using MassTransit;
using Haworks.Contracts.Payments;
using Haworks.Notifications.Application.Commands;
using Haworks.Notifications.Domain.Enums;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Haworks.Notifications.Application.Consumers;

public sealed class RefundEmailConsumer(
    IMediator mediator,
    ILogger<RefundEmailConsumer> logger) :
    IConsumer<RefundCompletedEvent>,
    IConsumer<RefundFailedEvent>,
    IConsumer<RefundStalledEvent>
{
    public async Task Consume(ConsumeContext<RefundCompletedEvent> context)
    {
        var msg = context.Message;
        var recipient = msg.CustomerEmail;
        if (string.IsNullOrWhiteSpace(recipient))
        {
            logger.LogWarning("RefundCompletedEvent for {RefundId} has no CustomerEmail; skipping email notification", msg.RefundId);
            return;
        }

        await mediator.Send(new SendNotificationCommand(
            UserId: null,
            Recipient: recipient,
            Channel: NotificationChannel.Email,
            TemplateId: "refund-completed",
            Priority: NotificationPriority.High,
            Variables: new Dictionary<string, object>
            {
                ["RefundId"] = msg.RefundId,
                ["Amount"] = msg.AmountCents / 100m,
                ["Currency"] = msg.Currency
            },
            IdempotencyKey: $"refund-completed-{msg.RefundId}"
        ));
    }

    public async Task Consume(ConsumeContext<RefundFailedEvent> context)
    {
        var msg = context.Message;
        var recipient = msg.CustomerEmail;
        if (string.IsNullOrWhiteSpace(recipient))
        {
            logger.LogWarning("RefundFailedEvent for {RefundId} has no CustomerEmail; skipping email notification", msg.RefundId);
            return;
        }

        await mediator.Send(new SendNotificationCommand(
            UserId: null,
            Recipient: recipient,
            Channel: NotificationChannel.Email,
            TemplateId: "refund-failed",
            Priority: NotificationPriority.High,
            Variables: new Dictionary<string, object>
            {
                ["RefundId"] = msg.RefundId,
                ["Reason"] = msg.FailureDetail
            },
            IdempotencyKey: $"refund-failed-{msg.RefundId}"
        ));
    }

    public Task Consume(ConsumeContext<RefundStalledEvent> context)
    {
        // This usually goes to Ops, not the customer
        logger.LogWarning("Refund {RefundId} is stalled! Notify Ops.", context.Message.RefundId);
        return Task.CompletedTask;
    }
}
