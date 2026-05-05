using System.Diagnostics;
using Haworks.Catalog.Application.DTOs;
using Haworks.Catalog.Application.Interfaces;
using Haworks.Catalog.Domain.Interfaces;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;

namespace Haworks.Catalog.Infrastructure.Caching;

/// <summary>
/// HybridCache-backed read-through cache over
/// <see cref="IProductRepository.GetByIdAsync"/>. The factory invocation
/// hits real Postgres via the same repository production callers use;
/// HybridCache's built-in singleflight collapses concurrent misses for
/// the same key into one DB read.
///
/// Cache key format: <c>product:{guid}</c>. TTL is HybridCache's default
/// (5 minutes) — production tuning would set it explicitly per workload,
/// but for the catalog read-mostly pattern the default is reasonable.
///
/// Source detection: HybridCache doesn't expose hit/miss info, so we set
/// a captured boolean to false from inside the factory. If the factory
/// ran, we observed a cache miss; otherwise the call was served from L1.
/// </summary>
public sealed class ProductCacheReader(
    Microsoft.Extensions.Caching.Hybrid.HybridCache cache,
    IProductRepository repository,
    ILogger<ProductCacheReader> logger) : IProductCacheReader
{
    private const string CacheKeyPrefix = "product:";

    public async Task<ProductCacheReadResult> GetAsync(Guid productId, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var hit = true;

        var dto = await cache.GetOrCreateAsync<ProductDto?>(
            CacheKeyPrefix + productId,
            async (token) =>
            {
                hit = false;
                var product = await repository.GetByIdAsync(productId, token);
                if (product is null)
                {
                    return null;
                }
                return new ProductDto(
                    product.Id,
                    product.Name,
                    product.Description,
                    product.UnitPrice,
                    product.StockQuantity,
                    product.IsInStock,
                    product.IsListed,
                    product.CategoryId,
                    product.Category?.Name);
            },
            cancellationToken: ct);

        sw.Stop();
        var source = dto is null ? "not_found" : (hit ? "L1" : "database");
        return new ProductCacheReadResult(dto, source, sw.ElapsedMilliseconds);
    }

    public async Task InvalidateAsync(Guid productId, CancellationToken ct)
    {
        await cache.RemoveAsync(CacheKeyPrefix + productId, ct);
        logger.LogDebug("Product cache invalidated: {ProductId}", productId);
    }
}
