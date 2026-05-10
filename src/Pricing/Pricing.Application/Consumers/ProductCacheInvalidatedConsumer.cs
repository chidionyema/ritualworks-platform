using MassTransit;
using Microsoft.Extensions.Logging;
using Haworks.Contracts.Catalog;
using Microsoft.Extensions.Caching.Distributed;
using System.Threading.Tasks;

namespace Haworks.Pricing.Application.Consumers;

public sealed class ProductCacheInvalidatedConsumer(
    IDistributedCache cache,
    ILogger<ProductCacheInvalidatedConsumer> logger
) : IConsumer<ProductCacheInvalidatedEvent>
{
    public async Task Consume(ConsumeContext<ProductCacheInvalidatedEvent> context)
    {
        var evt = context.Message;
        logger.LogInformation("Invalidating cache for product {ProductId} due to {Reason}", evt.ProductId, evt.Reason);
        
        var key = $"pricing:product:{evt.ProductId}";
        await cache.RemoveAsync(key, context.CancellationToken);
    }
}
