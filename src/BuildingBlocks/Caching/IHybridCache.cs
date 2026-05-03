namespace Haworks.BuildingBlocks.Caching;

/// <summary>
/// Hybrid cache with L1 (in-memory) and L2 (distributed) layers.
/// Provides automatic fallback and stampede protection.
///
/// Cache flow:
/// 1. Check L1 (memory) - fast path, sub-ms
/// 2. Check L2 (distributed/Redis) - network round-trip
/// 3. Call factory with stampede protection - only one caller hits DB
/// 4. Populate both L1 and L2 with result
/// </summary>
public interface IHybridCache
{
    /// <summary>
    /// Gets a value from cache or creates it using the factory.
    /// L1 miss → L2 check → Factory call (with stampede protection)
    /// </summary>
    /// <typeparam name="T">The type of cached value.</typeparam>
    /// <param name="key">Cache key.</param>
    /// <param name="factory">Factory function called on cache miss.</param>
    /// <param name="options">Optional cache behavior configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Cached or newly created value, or null if factory returns null.</returns>
    ValueTask<T?> GetOrCreateAsync<T>(
        string key,
        Func<CancellationToken, Task<T?>> factory,
        HybridCacheOptions? options = null,
        CancellationToken ct = default) where T : class;

    /// <summary>
    /// Gets a value from cache without factory fallback.
    /// Checks L1 first, then L2.
    /// </summary>
    /// <typeparam name="T">The type of cached value.</typeparam>
    /// <param name="key">Cache key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Cached value or null if not found.</returns>
    ValueTask<T?> GetAsync<T>(string key, CancellationToken ct = default) where T : class;

    /// <summary>
    /// Sets a value in both L1 and L2 caches.
    /// </summary>
    /// <typeparam name="T">The type of value to cache.</typeparam>
    /// <param name="key">Cache key.</param>
    /// <param name="value">Value to cache.</param>
    /// <param name="options">Optional cache behavior configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask SetAsync<T>(
        string key,
        T value,
        HybridCacheOptions? options = null,
        CancellationToken ct = default) where T : class;

    /// <summary>
    /// Removes a value from both L1 and L2 caches.
    /// </summary>
    /// <param name="key">Cache key to remove.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask RemoveAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// Removes all keys matching a prefix pattern from both cache layers.
    /// Use for bulk invalidation (e.g., all products in a category).
    /// </summary>
    /// <param name="prefix">Key prefix to match.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask RemoveByPrefixAsync(string prefix, CancellationToken ct = default);
}

/// <summary>
/// Options for hybrid cache entry behavior.
/// </summary>
public sealed record HybridCacheOptions
{
    /// <summary>
    /// L1 (memory) cache duration.
    /// Shorter duration reduces memory usage but increases L2 hits.
    /// Default: 1 minute.
    /// </summary>
    public TimeSpan? L1Duration { get; init; }

    /// <summary>
    /// L2 (distributed) cache duration.
    /// Longer duration reduces database load but may serve stale data.
    /// Default: 5 minutes.
    /// </summary>
    public TimeSpan? L2Duration { get; init; }

    /// <summary>
    /// Skip L2 cache (memory-only mode).
    /// Use for data that doesn't need to be shared across instances.
    /// Default: false.
    /// </summary>
    public bool SkipDistributed { get; init; }

    /// <summary>
    /// Skip L1 cache (distributed-only mode).
    /// Use when memory is constrained or data changes frequently.
    /// Default: false.
    /// </summary>
    public bool SkipMemory { get; init; }

    /// <summary>
    /// Default options: L1=1min, L2=5min, both layers enabled.
    /// </summary>
    public static HybridCacheOptions Default => new();

    /// <summary>
    /// Short-lived cache for frequently changing data.
    /// L1=30s, L2=1min.
    /// </summary>
    public static HybridCacheOptions ShortLived => new()
    {
        L1Duration = TimeSpan.FromSeconds(30),
        L2Duration = TimeSpan.FromMinutes(1)
    };

    /// <summary>
    /// Long-lived cache for stable reference data.
    /// L1=5min, L2=30min.
    /// </summary>
    public static HybridCacheOptions LongLived => new()
    {
        L1Duration = TimeSpan.FromMinutes(5),
        L2Duration = TimeSpan.FromMinutes(30)
    };

    /// <summary>
    /// Memory-only cache (no distributed layer).
    /// Use for instance-specific data.
    /// </summary>
    public static HybridCacheOptions MemoryOnly => new()
    {
        SkipDistributed = true
    };

    /// <summary>
    /// Distributed-only cache (no memory layer).
    /// Use when memory is constrained.
    /// </summary>
    public static HybridCacheOptions DistributedOnly => new()
    {
        SkipMemory = true
    };
}
