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
        // In a real app, we'd fetch the recipient email from the Order/Payment service 
        // or have it passed in the event. For this demo, we'll use a placeholder
        // or assume the system can resolve it.
        
        await mediator.Send(new SendNotificationCommand(
            UserId: null, // Transparent for now
            Recipient: "customer@example.com", // Placeholder
            Channel: NotificationChannel.Email,
            TemplateId: "refund-completed",
            Priority: NotificationPriority.High,
            Variables: new Dictionary<string, object>
            {
                ["RefundId"] = msg.RefundId,
                ["Amount"] = msg.Amount,
                ["Currency"] = msg.Currency
            },
            IdempotencyKey: $"refund-completed-{msg.RefundId}"
        ));
    }

    public async Task Consume(ConsumeContext<RefundFailedEvent> context)
    {
        var msg = context.Message;
        await mediator.Send(new SendNotificationCommand(
            UserId: null,
            Recipient: "customer@example.com",
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

    public async Task Consume(ConsumeContext<RefundStalledEvent> context)
    {
        // This usually goes to Ops, not the customer
        logger.LogWarning("Refund {RefundId} is stalled! Notify Ops.", context.Message.RefundId);
        await Task.CompletedTask;
    }
}
