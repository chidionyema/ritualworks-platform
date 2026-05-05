using Haworks.BffWeb.Application.Interfaces;
using Haworks.Contracts.Catalog;
using MassTransit;

namespace Haworks.BffWeb.Api.SignalR;

/// <summary>
/// Bridges <see cref="ProductCacheInvalidatedEvent"/> (published by
/// catalog-svc's real <c>UpdateProductCommandHandler</c> /
/// <c>DeleteProductCommandHandler</c> through the EF outbox after the row
/// commits) to a SignalR <c>OnCacheEvent</c> push scoped to the demo
/// session.
///
/// Only fires when <see cref="ProductCacheInvalidatedEvent.CorrelationId"/>
/// is set — production callers (non-demo) emit the event with a null
/// correlation, and we drop those rather than push to no-one.
/// </summary>
public sealed class ProductCacheInvalidatedBridge(
    IDemoHubNotifier notifier,
    ILogger<ProductCacheInvalidatedBridge> logger) : IConsumer<ProductCacheInvalidatedEvent>
{
    public Task Consume(ConsumeContext<ProductCacheInvalidatedEvent> ctx)
    {
        var evt = ctx.Message;
        if (evt.CorrelationId is null)
        {
            logger.LogInformation(
                "ProductCacheInvalidatedEvent for {ProductId} has no CorrelationId; nothing to push",
                evt.ProductId);
            return Task.CompletedTask;
        }

        logger.LogInformation(
            "Bridging ProductCacheInvalidatedEvent -> OnCacheEvent for session={CorrelationId} product={ProductId} reason={Reason}",
            evt.CorrelationId, evt.ProductId, evt.Reason);

        return notifier.NotifyCacheEventAsync(new CacheEvent(
            SessionId: evt.CorrelationId.Value,
            Action: "invalidate",
            Key: $"product:{evt.ProductId}",
            Result: evt.Reason,
            LatencyMs: null,
            Timestamp: DateTime.UtcNow), ctx.CancellationToken);
    }
}
