using MassTransit;
using Haworks.Contracts.Catalog;

namespace Haworks.Catalog.Application.Consumers;

/// <summary>
/// Compensation consumer. Triggered by checkout-svc' CheckoutSaga when a
/// payment fails AFTER stock was reserved. Reverses each reserved item via
/// <see cref="Product.ReleaseStock"/> and publishes
/// <see cref="StockReleasedEvent"/> for downstream observers.
///
/// Idempotency layers:
///   1. MT inbox dedupes by MessageId (transport-level) — the saga's
///      publish gets a deterministic MessageId via MT's own outbox; inbox
///      catches replays.
///   2. xmin shadow concurrency on Product catches concurrent writers; if
///      a reserve and a release race, the loser throws
///      DbUpdateConcurrencyException and EF retry-on-failure handles it.
///
/// Per ADR-0009 the consumer touches no foreign-context state — only
/// catalog-svc's own Product aggregates.
/// </summary>
public sealed class StockReleaseRequestedConsumer(
    IProductRepository products,
    IDomainEventPublisher eventPublisher,
    ILogger<StockReleaseRequestedConsumer> logger
) : IConsumer<StockReleaseRequestedEvent>
{
    public async Task Consume(ConsumeContext<StockReleaseRequestedEvent> context)
    {
        var evt = context.Message;
        logger.LogInformation(
            "Releasing stock for orderId={OrderId}, sagaId={SagaId}, items={ItemCount}, reason={Reason}",
            evt.OrderId, evt.SagaId, evt.Items.Count, evt.Reason);

        // Idempotency: if the reservation for this order was already released, skip
        var existingReservation = await products.GetStockReservationByOrderIdAsync(evt.OrderId, context.CancellationToken);
        if (existingReservation is not null && existingReservation.ReleasedAt.HasValue)
        {
            logger.LogInformation(
                "Stock already released for orderId={OrderId}; idempotent skip",
                evt.OrderId);
            return;
        }

        foreach (var item in evt.Items)
        {
            var product = await products.GetByIdTrackedAsync(item.ProductId, context.CancellationToken);
            if (product is null)
            {
                logger.LogWarning(
                    "StockReleaseRequested references unknown product {ProductId}; skipping that item",
                    item.ProductId);
                continue;
            }

            // Domain-level guard: ReleaseStock(qty) requires qty > 0.
            // The saga only sends items it observed in StockReservedEvent,
            // so quantity should always be > 0 — but defensive null/zero
            // protection prevents a malformed event from blowing up the
            // whole release.
            if (item.Quantity <= 0) continue;

            product.ReleaseStock(item.Quantity);
        }

        // Publish BEFORE save — same outbox-friendly pattern as the rest
        // of the platform. The OutboxMessage row commits in the same EF
        // transaction as the stock increments; on rollback the publish
        // is rolled back too.
        await eventPublisher.PublishAsync(new StockReleasedEvent
        {
            OrderId = evt.OrderId,
            Items = evt.Items,
            Reason = evt.Reason,
        }, context.CancellationToken);

        await products.SaveChangesAsync(context.CancellationToken);
        logger.LogInformation("Released stock for orderId={OrderId}; published StockReleasedEvent", evt.OrderId);
    }
}
