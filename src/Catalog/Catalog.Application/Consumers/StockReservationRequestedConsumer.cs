using MassTransit;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Haworks.Catalog.Application.Telemetry;
using Haworks.Contracts.Catalog;
using Haworks.Contracts.Checkout;

namespace Haworks.Catalog.Application.Consumers;

/// <summary>
/// Saga choreography role: drives the
/// <c>Initiated → StockReserved | Abandoned</c> transition of
/// <c>CheckoutSaga</c> (in
/// src/CheckoutOrchestrator/CheckoutOrchestrator.Application/Sagas/
/// CheckoutSaga.cs — no project reference per ADR-0009 bounded-context
/// isolation, so the cref is text-only).
///
/// The saga's <c>Initially</c> block (CheckoutSaga.cs around line 71)
/// publishes <see cref="StockReservationRequestedEvent"/> when it transitions
/// from Initial to Initiated. This consumer picks up that event from
/// RabbitMQ, reserves each requested item via
/// <see cref="Product.ReserveStock"/>, and publishes EITHER:
///
/// <list type="bullet">
///   <item><see cref="StockReservedEvent"/> — all items reserved → saga
///         transitions Initiated → StockReserved (CheckoutSaga.cs around
///         line 100, "During(Initiated, When(StockReserved)...)")</item>
///   <item><see cref="StockReservationFailedEvent"/> — any item
///         insufficient → saga transitions Initiated → Abandoned (no
///         compensation needed, nothing was reserved)</item>
/// </list>
///
/// Idempotency: MT inbox dedupes transport replays; EF xmin shadow
/// concurrency catches concurrent writers (UPDATE Products SET ...
/// WHERE xmin = N — same mechanism the ConcurrencyDemo demonstrates).
///
/// Per ADR-0009 the consumer touches no foreign-context state: only
/// catalog Product aggregates. All cross-context fields needed
/// downstream (TotalAmount, CustomerEmail, OrderLineItems, …) are
/// propagated forward on the published event so PaymentSession can act
/// without querying out.
/// </summary>
public sealed class StockReservationRequestedConsumer(
    IProductRepository products,
    IDomainEventPublisher eventPublisher,
    ILogger<StockReservationRequestedConsumer> logger
) : IConsumer<StockReservationRequestedEvent>
{
    public async Task Consume(ConsumeContext<StockReservationRequestedEvent> context)
    {
        var evt = context.Message;

        using var activity = CatalogActivities.Source.StartActivity("catalog.reservation.create");
        activity?.SetTag("order.id", evt.OrderId);
        activity?.SetTag("saga.id", evt.SagaId);
        activity?.SetTag("reservation.item_count", evt.Items.Count);
        activity?.SetTag("reservation.total_quantity", evt.Items.Sum(i => i.Quantity));

        logger.LogInformation(
            "Reserving stock for orderId={OrderId}, sagaId={SagaId}, items={ItemCount}",
            evt.OrderId, evt.SagaId, evt.Items.Count);

        // Idempotency: if a reservation already exists for this order, skip processing.
        // This check runs inside the ambient MassTransit EF Outbox transaction.
        var existingReservation = await products.GetStockReservationByOrderIdAsync(evt.OrderId, context.CancellationToken);
        if (existingReservation is not null)
        {
            logger.LogInformation(
                "Reservation already exists for orderId={OrderId}; idempotent skip",
                evt.OrderId);
            return;
        }

        try
        {
            await ReserveStockCoreAsync(evt, context);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            // Defense-in-depth: concurrent duplicate slipped past the read check above.
            // The reservation was already persisted by the winner — treat as idempotent success.
            logger.LogInformation(
                "Unique constraint on reservation for orderId={OrderId}; concurrent duplicate — idempotent success",
                evt.OrderId);
        }
    }

    private async Task ReserveStockCoreAsync(StockReservationRequestedEvent evt, ConsumeContext<StockReservationRequestedEvent> context)
    {
        var reserved = new List<StockReservationItem>(evt.Items.Count);
        var failed = new List<FailedReservationItem>();

        foreach (var item in evt.Items)
        {
            var product = await products.GetByIdTrackedAsync(item.ProductId, context.CancellationToken);
            if (product is null)
            {
                failed.Add(new FailedReservationItem
                {
                    ProductId = item.ProductId,
                    ProductName = item.ProductName,
                    RequestedQuantity = item.Quantity,
                    AvailableQuantity = 0,
                });
                continue;
            }

            if (!product.ReserveStock(item.Quantity))
            {
                failed.Add(new FailedReservationItem
                {
                    ProductId = item.ProductId,
                    ProductName = product.Name,
                    RequestedQuantity = item.Quantity,
                    AvailableQuantity = product.StockQuantity,
                });
                continue;
            }

            reserved.Add(new StockReservationItem
            {
                ProductId = product.Id,
                ProductName = product.Name,
                Quantity = item.Quantity,
                RemainingStock = product.StockQuantity,
            });
        }

        if (failed.Count > 0)
        {
            // Don't SaveChanges — EF tracker discards the partial mutations
            // when the consume scope ends, so no stock is silently held.
            logger.LogWarning(
                "Stock reservation failed for orderId={OrderId}; {FailedCount}/{TotalCount} items unavailable",
                evt.OrderId, failed.Count, evt.Items.Count);

            await eventPublisher.PublishAsync(new StockReservationFailedEvent
            {
                OrderId = evt.OrderId,
                SagaId = evt.SagaId,
                FailedItems = failed,
                Reason = $"{failed.Count} of {evt.Items.Count} items unavailable",
            }, context.CancellationToken);
            return;
        }

        // Publish BEFORE save — outbox-friendly. The OutboxMessage row commits
        // in the same EF transaction as the stock decrements; on rollback the
        // publish is rolled back too.
        await eventPublisher.PublishAsync(new StockReservedEvent
        {
            OrderId = evt.OrderId,
            SagaId = evt.SagaId,
            UserId = evt.UserId,
            TotalAmount = evt.TotalAmount,
            Currency = evt.Currency,
            CustomerEmail = evt.CustomerEmail,
            IdempotencyKey = evt.IdempotencyKey,
            Items = reserved,
            OrderLineItems = evt.Items,
        }, context.CancellationToken);

        // MassTransit EF Outbox commits automatically
        logger.LogInformation(
            "Reserved stock for orderId={OrderId}; published StockReservedEvent ({Total} units)",
            evt.OrderId, reserved.Sum(i => i.Quantity));
    }
}
