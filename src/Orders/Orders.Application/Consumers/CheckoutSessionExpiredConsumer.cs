using MassTransit;
using Microsoft.Extensions.Logging;
using Haworks.Contracts.Catalog;
using Haworks.Contracts.Payments;
using Haworks.Orders.Domain;
using Haworks.Orders.Domain.Interfaces;
using Haworks.BuildingBlocks.Messaging;

namespace Haworks.Orders.Application.Consumers;

/// <summary>
/// Consumes <see cref="CheckoutSessionExpiredEvent"/> from payments-svc (via Stripe webhook)
/// and marks the matching Order as <see cref="OrderStatus.Expired"/>, then publishes
/// <see cref="StockReleaseRequestedEvent"/> to return reserved inventory to catalog.
/// </summary>
public sealed class CheckoutSessionExpiredConsumer(
    IOrderRepository orders,
    IDomainEventPublisher eventPublisher,
    ILogger<CheckoutSessionExpiredConsumer> logger
) : IConsumer<CheckoutSessionExpiredEvent>
{
    public async Task Consume(ConsumeContext<CheckoutSessionExpiredEvent> context)
    {
        var evt = context.Message;
        
        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["OrderId"] = evt.OrderId,
            ["PaymentId"] = evt.PaymentId,
            ["SessionId"] = evt.SessionId
        });

        logger.LogInformation("Processing CheckoutSessionExpiredEvent for order {OrderId}", evt.OrderId);

        var order = await orders.GetByIdTrackedAsync(evt.OrderId, context.CancellationToken);
        if (order is null)
        {
            logger.LogWarning("Order {OrderId} not found for expired session {SessionId}", evt.OrderId, evt.SessionId);
            return;
        }

        if (order.Status is OrderStatus.Expired or OrderStatus.Paid or OrderStatus.Abandoned)
        {
            logger.LogInformation("Order {OrderId} already in terminal status {Status}, skipping", order.Id, order.Status);
            return;
        }

        // Atomically mark order expired.
        // The MarkStockReleasedAsync repository method uses ExecuteUpdateAsync for a 
        // high-performance, atomic status transition.
        var wasMarked = await orders.MarkStockReleasedAsync(
            order.Id, 
            OrderStatus.Expired, 
            "checkout_session_expired", 
            context.CancellationToken);

        if (!wasMarked)
        {
            logger.LogInformation("Order {OrderId} already processed or in terminal state, skipping", order.Id);
            return;
        }

        // Publish stock release requested event for catalog-svc.
        await eventPublisher.PublishAsync(new StockReleaseRequestedEvent
        {
            OrderId = order.Id,
            SagaId = order.SagaId,
            Reason = "checkout_session_expired",
            Items = order.Items.Select(i => new StockReservationItem
            {
                ProductId = i.ProductId,
                ProductName = i.ProductName,
                Quantity = i.Quantity,
                RemainingStock = null
            }).ToList()
        }, context.CancellationToken);

        // ExecuteUpdateAsync bypasses EF change tracking, so the outbox rows
        // written by PublishAsync have NOT been flushed yet. Explicit
        // SaveChangesAsync ensures the outbox message is committed to the DB
        // so the BusOutboxDeliveryService can pick it up.
        await orders.SaveChangesAsync(context.CancellationToken);

        logger.LogInformation("Order {OrderId} marked Expired; published StockReleaseRequestedEvent", order.Id);
    }
}
