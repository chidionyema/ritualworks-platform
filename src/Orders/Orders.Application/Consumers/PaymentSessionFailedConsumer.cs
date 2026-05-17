using MassTransit;
using Haworks.Contracts.Catalog;
using Haworks.Contracts.Orders;
using Haworks.Contracts.Payments;

namespace Haworks.Orders.Application.Consumers;

/// <summary>
/// Consumes <see cref="PaymentSessionFailedEvent"/> from payments-svc
/// (gateway rejected / session expired terminally) and abandons the
/// matching Order. Publishes <see cref="OrderAbandonedEvent"/> for
/// downstream consumers (stock release back to inventory, recovery email).
/// </summary>
public sealed class PaymentSessionFailedConsumer(
    IOrderRepository orders,
    IDomainEventPublisher eventPublisher,
    ILogger<PaymentSessionFailedConsumer> logger
) : IConsumer<PaymentSessionFailedEvent>
{
    public async Task Consume(ConsumeContext<PaymentSessionFailedEvent> context)
    {
        var evt = context.Message;
        logger.LogInformation(
            "Processing PaymentSessionFailedEvent: orderId={OrderId}, sagaId={SagaId}, reason={ErrorCode}",
            evt.OrderId, evt.SagaId, evt.ErrorCode);

        var order = await orders.GetByIdTrackedAsync(evt.OrderId, context.CancellationToken);
        if (order is null)
        {
            logger.LogWarning("Order {OrderId} not found for PaymentSessionFailedEvent; ignoring", evt.OrderId);
            return;
        }

        var previousStatus = order.Status.ToString();
        if (!order.MarkAbandoned($"PaymentSessionFailed: {evt.ErrorCode} — {evt.ErrorMessage}"))
        {
            logger.LogInformation(
                "Order {OrderId} already in terminal status {Status}; skipping OrderAbandoned publish",
                order.Id, order.Status);
            return;
        }

        await eventPublisher.PublishAsync(new OrderAbandonedEvent
        {
            OrderId = order.Id,
            SagaId = order.SagaId,
            Items = order.Items.Select(i => new StockReservationItem
            {
                ProductId = i.ProductId,
                ProductName = i.ProductName,
                Quantity = i.Quantity,
                RemainingStock = null,
            }).ToList(),
            AgeAtAbandonment = DateTime.UtcNow - order.CreatedAt,
            PreviousStatus = previousStatus,
            CustomerEmail = order.CustomerEmail,
        }, context.CancellationToken);

        // MassTransit EF Outbox commits automatically
        logger.LogInformation("Order {OrderId} marked Abandoned (PaymentSessionFailed)", order.Id);
    }
}
