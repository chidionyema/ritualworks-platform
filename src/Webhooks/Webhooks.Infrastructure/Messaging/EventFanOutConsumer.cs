using Haworks.Contracts.Orders;
using Haworks.Contracts.Payments;
using Haworks.Webhooks.Domain;
using Haworks.Webhooks.Application.Interfaces;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Hangfire;
using Microsoft.Extensions.Logging;

namespace Haworks.Webhooks.Infrastructure.Messaging;

public sealed class EventFanOutConsumer(
    IWebhooksDbContext db,
    IBackgroundJobClient jobClient,
    ILogger<EventFanOutConsumer> logger) :
    IConsumer<OrderCreatedEvent>,
    IConsumer<OrderCompletedEvent>,
    IConsumer<OrderAbandonedEvent>,
    IConsumer<PaymentCompletedEvent>,
    IConsumer<RefundIssuedEvent>
{
    public Task Consume(ConsumeContext<OrderCreatedEvent> context) => FanOutAsync(context, "order.created", context.Message);
    public Task Consume(ConsumeContext<OrderCompletedEvent> context) => FanOutAsync(context, "order.completed", context.Message);
    public Task Consume(ConsumeContext<OrderAbandonedEvent> context) => FanOutAsync(context, "order.abandoned", context.Message);
    public Task Consume(ConsumeContext<PaymentCompletedEvent> context) => FanOutAsync(context, "payment.completed", context.Message);
    public Task Consume(ConsumeContext<RefundIssuedEvent> context) => FanOutAsync(context, "refund.issued", context.Message);

    private async Task FanOutAsync<T>(ConsumeContext<T> context, string externalEventName, T data) where T : class
    {
        // Idempotency: unique index on (SubscriptionId, EventId) in WebhooksDbContext prevents duplicate delivery
        var eventId = context.MessageId?.ToString() ?? Guid.NewGuid().ToString();
        
        // 1. Resolve subscriptions
        var subscriptions = await db.Subscriptions
            .Where(s => s.IsActive && s.DeletedAt == null && s.Events.Contains(externalEventName))
            .ToListAsync(context.CancellationToken);

        if (subscriptions.Count == 0) return;

        logger.LogInformation("Fanning out event {EventName} to {Count} subscriptions", externalEventName, subscriptions.Count);

        // 2. Prepare payload
        var payload = JsonSerializer.Serialize(new
        {
            @event = externalEventName,
            id = $"evt_{DateTime.UtcNow:yyyy-MM-ddTHH-mm-ss}_{eventId[..8]}",
            deliveredAt = DateTime.UtcNow,
            data
        });

        var deliveries = new List<WebhookDelivery>();
        foreach (var sub in subscriptions)
        {
            var delivery = new WebhookDelivery(sub.Id, eventId, externalEventName, payload);
            db.Deliveries.Add(delivery);
            deliveries.Add(delivery);
        }

        await db.SaveChangesAsync(context.CancellationToken);

        foreach (var delivery in deliveries)
        {
            jobClient.Enqueue<IWebhookDispatcher>(x => x.DispatchAsync(delivery.Id, CancellationToken.None));
        }
    }
}
