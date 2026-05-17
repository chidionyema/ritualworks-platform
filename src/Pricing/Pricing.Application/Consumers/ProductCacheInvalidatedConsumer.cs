using Haworks.Contracts.Catalog;
using MassTransit;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Haworks.Pricing.Application.Consumers;

/// <summary>
/// Listens for ProductCacheInvalidatedEvent from catalog-svc and evicts
/// the corresponding IMemoryCache entry so subsequent price calculations
/// fetch fresh product data.
/// </summary>
public sealed class ProductCacheInvalidatedConsumer : IConsumer<ProductCacheInvalidatedEvent>
{
    private readonly IMemoryCache _cache;
    private readonly ILogger<ProductCacheInvalidatedConsumer> _logger;

    public ProductCacheInvalidatedConsumer(IMemoryCache cache, ILogger<ProductCacheInvalidatedConsumer> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    public Task Consume(ConsumeContext<ProductCacheInvalidatedEvent> context)
    {
        var productId = context.Message.ProductId;
        var cacheKey = $"catalog_price_{productId}";

        _cache.Remove(cacheKey);

        _logger.LogInformation(
            "Evicted cache entry {CacheKey} due to ProductCacheInvalidatedEvent (reason={Reason})",
            cacheKey, context.Message.Reason);

        return Task.CompletedTask;
    }
}
