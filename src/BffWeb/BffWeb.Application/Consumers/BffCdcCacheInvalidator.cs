using Haworks.Contracts.Cdc;
using MassTransit;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;

namespace Haworks.BffWeb.Application.Consumers;

public sealed class BffCdcCacheInvalidator : IConsumer<EntityChangedEvent>
{
    private readonly IDistributedCache _cache;
    private readonly ILogger<BffCdcCacheInvalidator> _logger;

    public BffCdcCacheInvalidator(IDistributedCache cache, ILogger<BffCdcCacheInvalidator> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<EntityChangedEvent> context)
    {
        var msg = context.Message;

        // Invalidate BffWeb's product detail cache
        if (string.Equals(msg.SourceService, "catalog", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(msg.EntityType, "Products", StringComparison.OrdinalIgnoreCase))
        {
            var cacheKey = $"product_detail_{msg.EntityId}";
            await _cache.RemoveAsync(cacheKey, context.CancellationToken);
            _logger.LogInformation("CDC: Invalidated BFF cache for product {ProductId}", msg.EntityId);
        }
    }
}
