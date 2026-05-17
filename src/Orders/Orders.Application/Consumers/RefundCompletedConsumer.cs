using MassTransit;
using Haworks.Contracts.Payments;
using Haworks.Orders.Domain.Interfaces;
using Haworks.Orders.Domain;
using Microsoft.Extensions.Logging;

namespace Haworks.Orders.Application.Consumers;

/// <summary>
/// Idempotency: dual protection via (1) domain guard — MarkRefunded() returns
/// false if the order is not in Paid status, and (2) MassTransit inbox dedup
/// configured in OrdersConsumerDefinition. No IdempotentConsumerBase needed.
/// </summary>
public sealed class RefundCompletedConsumer(
    IOrderRepository orderRepository,
    ILogger<RefundCompletedConsumer> logger) : IConsumer<RefundCompletedEvent>
{
    public async Task Consume(ConsumeContext<RefundCompletedEvent> context)
    {
        var msg = context.Message;
        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["OrderId"] = msg.OrderId,
            ["RefundId"] = msg.RefundId
        });

        var order = await orderRepository.GetByIdTrackedAsync(msg.OrderId, context.CancellationToken);
        if (order == null)
        {
            logger.LogWarning("Order {OrderId} not found for refund {RefundId}", msg.OrderId, msg.RefundId);
            return;
        }

        if (order.MarkRefunded())
        {
            // MassTransit EF Outbox commits automatically
            logger.LogInformation("Order {OrderId} status updated to Refunded", msg.OrderId);
        }
    }
}
