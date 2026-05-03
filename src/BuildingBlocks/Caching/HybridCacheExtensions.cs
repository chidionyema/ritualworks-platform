using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Haworks.BuildingBlocks.Caching;

/// <summary>
/// DI registration helpers for the L1+L2 hybrid cache.
/// </summary>
public static class HybridCacheExtensions
{
    /// <summary>
    /// Registers <see cref="IHybridCache"/> with an in-memory L1 backed by
    /// a distributed L2. Caller is responsible for registering
    /// <see cref="IDistributedCache"/> via the appropriate package
    /// (e.g., <c>AddStackExchangeRedisCache</c>).
    ///
    /// In dev with no L2 wired, <see cref="AddInMemoryDistributedCache"/>
    /// can be used for a single-process fallback.
    /// </summary>
    public static IServiceCollection AddHybridCache(this IServiceCollection services)
    {
        services.AddMemoryCache();
        services.AddSingleton<IHybridCache, HybridCache>();
        return services;
    }

    /// <summary>
    /// Registers an in-memory <see cref="IDistributedCache"/> as the L2
    /// backing store. Useful for single-process tests / dev where Redis
    /// isn't available. Production should register a real distributed
    /// cache (e.g., <c>AddStackExchangeRedisCache</c>) instead.
    /// </summary>
    public static IServiceCollection AddInMemoryDistributedCache(this IServiceCollection services)
    {
        services.AddDistributedMemoryCache();
        return services;
    }
}
