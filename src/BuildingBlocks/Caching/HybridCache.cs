using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text.Json;
using Haworks.BuildingBlocks.Caching;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Haworks.BuildingBlocks.Caching;

public sealed class HybridCache : IHybridCache, IDisposable
{
    private readonly IMemoryCache _l1Cache;
    private readonly IDistributedCache _l2Cache;
    private readonly ILogger<HybridCache> _logger;

    // LESSON 1: STRIPED LOCKING
    // We use a fixed number of semaphores (Stripes). Hashing the cache key to a stripe
    // provides "good enough" concurrency while strictly bounding memory usage.
    // This eliminates the need for a Cleanup() method and avoids ObjectDisposedException.
    private const int LockStripes = 1024;
    private readonly SemaphoreSlim[] _lockPool;

    private readonly ConcurrentDictionary<string, byte> _l1Keys = new();
    private const int L1KeysMaxSize = 10_000;
    private static readonly TimeSpan DefaultL1Duration = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);

    public HybridCache(IMemoryCache l1Cache, IDistributedCache l2Cache, ILogger<HybridCache> logger)
    {
        _l1Cache = l1Cache;
        _l2Cache = l2Cache;
        _logger = logger;

        _lockPool = new SemaphoreSlim[LockStripes];
        for (int i = 0; i < LockStripes; i++)
        {
            _lockPool[i] = new SemaphoreSlim(1, 1);
        }
    }

    // LESSON 2: VALUETASK FOR THE "HOT PATH"
    // Most cache calls are L1 hits. ValueTask is a struct; it avoids a heap allocation
    // when the result is available synchronously, significantly reducing GC pressure.
    public async ValueTask<T?> GetOrCreateAsync<T>(
        string key,
        Func<CancellationToken, Task<T?>> factory,
        HybridCacheOptions? options = null,
        CancellationToken ct = default) where T : class
    {
        options ??= HybridCacheOptions.Default;

        // 1. L1 Fast Path (No lock, no async state machine if hit)
        if (!options.SkipMemory && _l1Cache.TryGetValue(key, out T? l1Value))
        {
            return l1Value;
        }

        // 2. L2 Network Path (Outside the lock to prevent thread pool starvation)
        if (!options.SkipDistributed)
        {
            var l2Value = await GetFromL2Async<T>(key, ct);
            if (l2Value is not null)
            {
                if (!options.SkipMemory) SetL1(key, l2Value, options.L1Duration ?? DefaultL1Duration);
                return l2Value;
            }
        }

        // 3. Lock & Factory (Stampede Protection)
        var @lock = GetStripe(key);
        bool lockAcquired = await @lock.WaitAsync(LockTimeout, ct);
        if (!lockAcquired)
        {
            _logger.LogWarning("Cache lock timeout for {Key}, returning default", key);
            return default;
        }

        try
        {
            // LESSON 3: INNER DOUBLE-CHECK (L1 ONLY)
            // We only re-check L1 inside the lock. If another thread just finished
            // the factory call, it would have promoted the result to L1.
            // We avoid re-checking L2 here because L2 is a network call; holding
            // a lock during network I/O is a scalability bottleneck.
            if (!options.SkipMemory && _l1Cache.TryGetValue(key, out T? secondaryL1Value))
            {
                return secondaryL1Value;
            }

            var value = await factory(ct);
            if (value is not null)
            {
                await SetInternalAsync(key, value, options, ct);
            }
            return value;
        }
        finally
        {
            @lock.Release();
        }
    }

    /// <summary>
    /// Gets a value from cache without invoking a factory. Checks L1 first
    /// (sync, sub-ms), then L2 (network). Promotes L2 hits to L1.
    /// ValueTask avoids allocation on the L1-hit fast path.
    /// </summary>
    public async ValueTask<T?> GetAsync<T>(string key, CancellationToken ct = default) where T : class
    {
        if (_l1Cache.TryGetValue(key, out T? l1Value))
        {
            return l1Value;
        }

        var l2Value = await GetFromL2Async<T>(key, ct);
        if (l2Value is not null)
        {
            SetL1(key, l2Value, DefaultL1Duration);
        }

        return l2Value;
    }

    /// <summary>
    /// Sets a value in both L1 and L2 (subject to options).
    /// </summary>
    public async ValueTask SetAsync<T>(
        string key,
        T value,
        HybridCacheOptions? options = null,
        CancellationToken ct = default) where T : class
    {
        options ??= HybridCacheOptions.Default;
        await SetInternalAsync(key, value, options, ct);
    }

    /// <summary>
    /// Removes a single key from both L1 and L2. L2 errors are swallowed
    /// for transient connectivity issues (graceful degradation).
    /// </summary>
    public async ValueTask RemoveAsync(string key, CancellationToken ct = default)
    {
        _l1Cache.Remove(key);
        _l1Keys.TryRemove(key, out _);

        try
        {
            await _l2Cache.RemoveAsync(key, ct);
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            _logger.LogWarning("L2 Cache (Redis) unavailable for remove key {Key}", key);
        }
    }

    public async ValueTask RemoveByPrefixAsync(string prefix, CancellationToken ct = default)
    {
        // LESSON 4: THE DISTRIBUTED INVALIDATION LIMITATION
        // This only clears L1 on THIS instance. In a production cluster, you would 
        // publish a message to Redis Pub/Sub here so other instances clear their L1.
        var keysToRemove = _l1Keys.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
            .ToList();

        foreach (var key in keysToRemove)
        {
            _l1Cache.Remove(key);
            _l1Keys.TryRemove(key, out _);
        }

        // DistributedCache (IDistributedCache) does not support Prefix removal.
        // To fix this properly, use "Cache Versioning" or a direct Redis Multiplexer.
        await Task.CompletedTask;
    }

    private async Task<T?> GetFromL2Async<T>(string key, CancellationToken ct) where T : class
    {
        try
        {
            var bytes = await _l2Cache.GetAsync(key, ct);
            return bytes is null ? null : JsonSerializer.Deserialize<T>(bytes);
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            _logger.LogWarning("L2 Cache (Redis) unavailable for key {Key}", key);
            return null; // Graceful degradation
        }
    }

    // LESSON 5: TYPE-BASED EXCEPTION FILTERING
    // Using 'is' checks or type comparisons is faster and more robust than string-parsing exception names.
    private static bool IsTransient(Exception ex) =>
        ex is SocketException or TimeoutException || 
        ex.GetType().Name.Contains("Redis", StringComparison.OrdinalIgnoreCase);

    private SemaphoreSlim GetStripe(string key)
    {
        // We use an unsigned int to ensure the modulo result is positive.
        uint index = (uint)key.GetHashCode() % LockStripes;
        return _lockPool[index];
    }

    private void SetL1<T>(string key, T value, TimeSpan duration) where T : class
    {
        _l1Cache.Set(key, value, duration);
        _l1Keys.TryAdd(key, 0);

        // Guard against unbounded growth. When the key set exceeds the cap, clear it entirely.
        // The MemoryCache has its own eviction policy; _l1Keys is only a tracking set so
        // losing it is safe — the next miss will repopulate from L2 as normal.
        if (_l1Keys.Count > L1KeysMaxSize)
        {
            _l1Keys.Clear();
            _logger.LogDebug("HybridCache: _l1Keys exceeded {Max} entries and was cleared.", L1KeysMaxSize);
        }
    }

    private async Task SetInternalAsync<T>(string key, T value, HybridCacheOptions options, CancellationToken ct) where T : class
    {
        if (!options.SkipMemory) SetL1(key, value, options.L1Duration ?? DefaultL1Duration);
        
        if (!options.SkipDistributed)
        {
            try
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
                await _l2Cache.SetAsync(key, bytes, new DistributedCacheEntryOptions 
                { 
                    AbsoluteExpirationRelativeToNow = options.L2Duration 
                }, ct);
            }
            catch (Exception ex) when (IsTransient(ex)) { /* Log & Degrade */ }
        }
    }

    public void Dispose()
    {
        foreach (var s in _lockPool) s.Dispose();
    }
}