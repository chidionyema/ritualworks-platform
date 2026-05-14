using MassTransit;
using Haworks.Contracts.Payments;
using Haworks.Orders.Domain.Interfaces;
using Haworks.Orders.Domain;
using Microsoft.Extensions.Logging;

namespace Haworks.Orders.Application.Consumers;

public sealed class RefundCancelledConsumer(
    IOrderRepository orderRepository,
    ILogger<RefundCancelledConsumer> logger) : IConsumer<RefundCancelledEvent>
{
    public async Task Consume(ConsumeContext<RefundCancelledEvent> context)
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
            logger.LogWarning("Order {OrderId} not found for cancelled refund {RefundId}", msg.OrderId, msg.RefundId);
            return;
        }

        if (order.RevertToPaid())
        {
            await orderRepository.SaveChangesAsync(context.CancellationToken);
            logger.LogInformation("Order {OrderId} status reverted to Paid after refund cancellation", msg.OrderId);
        }
    }
}
