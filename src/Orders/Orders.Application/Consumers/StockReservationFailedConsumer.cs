using MassTransit;
using Haworks.Contracts.Catalog;
using Haworks.Contracts.Orders;

namespace Haworks.Orders.Application.Consumers;

/// <summary>
/// Consumes <see cref="StockReservationFailedEvent"/> from catalog-svc and
/// abandons the matching Order — there's no point waiting for payment when
/// the inventory isn't reservable. Publishes <see cref="OrderAbandonedEvent"/>
/// (no stock-release sub-event needed because the upstream reservation never
/// succeeded; nothing to release).
/// </summary>
public sealed class StockReservationFailedConsumer(
    IOrderRepository orders,
    IDomainEventPublisher eventPublisher,
    ILogger<StockReservationFailedConsumer> logger
) : IConsumer<StockReservationFailedEvent>
{
    public async Task Consume(ConsumeContext<StockReservationFailedEvent> context)
    {
        var evt = context.Message;
        logger.LogInformation(
            "Processing StockReservationFailedEvent: orderId={OrderId}, sagaId={SagaId}, reason={Reason}",
            evt.OrderId, evt.SagaId, evt.Reason);

        var order = await orders.GetByIdTrackedAsync(evt.OrderId, context.CancellationToken);
        if (order is null)
        {
            logger.LogWarning("Order {OrderId} not found for StockReservationFailedEvent; ignoring", evt.OrderId);
            return;
        }

        var previousStatus = order.Status.ToString();
        if (!order.MarkAbandoned($"StockReservationFailed: {evt.Reason}"))
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
        logger.LogInformation("Order {OrderId} marked Abandoned (StockReservationFailed)", order.Id);
    }
}
