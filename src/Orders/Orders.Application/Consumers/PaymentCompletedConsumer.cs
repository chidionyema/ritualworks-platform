using MassTransit;
using Haworks.Contracts.Orders;
using Haworks.Contracts.Payments;
using Haworks.Orders.Application.Telemetry;

namespace Haworks.Orders.Application.Consumers;

/// <summary>
/// Consumes <see cref="PaymentCompletedEvent"/> from payments-svc and
/// transitions the matching Order to <see cref="OrderStatus.Paid"/>, then
/// publishes <see cref="OrderCompletedEvent"/> for downstream consumers
/// (fulfillment, notifications, analytics).
///
/// Idempotency layers:
///   1. MassTransit inbox (MessageId-based dedup at transport level).
///   2. Application: <see cref="Order.MarkPaid"/> returns false if the
///      order is already in a terminal state — we skip the publish and
///      log, so duplicate webhook redeliveries don't double-emit
///      OrderCompletedEvent.
///   3. EF: xmin shadow concurrency on Orders catches concurrent
///      transitions from another consumer (e.g., StockReservationFailed
///      racing PaymentCompleted on the same orderId) — second one throws
///      DbUpdateConcurrencyException and EF retry-on-failure handles it.
///
/// Per ADR-0009 the consumer touches no foreign-context state.
/// </summary>
public sealed class PaymentCompletedConsumer(
    IOrderRepository orders,
    IDomainEventPublisher eventPublisher,
    ILogger<PaymentCompletedConsumer> logger
) : IConsumer<PaymentCompletedEvent>
{
    public async Task Consume(ConsumeContext<PaymentCompletedEvent> context)
    {
        var evt = context.Message;

        using var activity = OrdersActivities.Source.StartActivity("orders.complete");
        activity?.SetTag("order.id", evt.OrderId);
        activity?.SetTag("payment.id", evt.PaymentId);
        activity?.SetTag("saga.id", evt.SagaId);

        logger.LogInformation(
            "Processing PaymentCompletedEvent: orderId={OrderId}, paymentId={PaymentId}, sagaId={SagaId}",
            evt.OrderId, evt.PaymentId, evt.SagaId);

        var order = await orders.GetByIdTrackedAsync(evt.OrderId, context.CancellationToken);
        if (order is null)
        {
            // Order not yet created in our DB — could be: PaymentCompletedEvent
            // arrived before the CreateOrderCommand committed (unlikely but
            // possible under heavy load), or a payment for an order that
            // belongs to a different orders-svc instance. Log + ack; the
            // caller's idempotency key path will eventually resolve.
            logger.LogWarning("Order {OrderId} not found for PaymentCompletedEvent; ignoring", evt.OrderId);
            return;
        }

        if (!order.MarkPaid(evt.PaymentId))
        {
            logger.LogInformation(
                "Order {OrderId} already in terminal status {Status}; skipping OrderCompleted publish",
                order.Id, order.Status);
            return;
        }

        // Publish BEFORE SaveChanges so the OutboxMessage row writes inside
        // the same EF transaction as the state change (production outbox
        // semantics). In tests with the in-memory harness the publish goes
        // straight to the bus — same observable behavior.
        await eventPublisher.PublishAsync(new OrderCompletedEvent
        {
            OrderId = order.Id,
            CustomerId = TryParseGuid(order.UserId),
            TotalAmount = order.TotalAmount,
            CustomerEmail = order.CustomerEmail,
            CompletedAt = order.LastModifiedDate ?? DateTime.UtcNow,
            PaymentId = evt.PaymentId,
        }, context.CancellationToken);

        // MassTransit EF Outbox commits automatically
        logger.LogInformation("Order {OrderId} marked Paid; published OrderCompletedEvent", order.Id);
    }

    /// <summary>
    /// OrderCompletedEvent.CustomerId is Guid? — orders-svc UserId is a
    /// string (ASP.NET Identity convention). When the user identifier is
    /// already a Guid (default Identity setup), return it; otherwise null.
    /// </summary>
    private static Guid? TryParseGuid(string userId) =>
        Guid.TryParse(userId, out var g) ? g : null;
}
