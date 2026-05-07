using Haworks.BuildingBlocks.Caching;
using Haworks.Payments.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace Haworks.Payments.Infrastructure.Stripe;

/// <summary>
/// Hybrid cache service for payment session validation.
/// Uses distributed-only caching (no L1) since payment sessions
/// are short-lived and must be consistent across instances.
/// </summary>
internal sealed class StripePaymentSessionCacheService : IPaymentSessionCache
{
    private readonly IHybridCache _cache;
    private readonly ILogger<StripePaymentSessionCacheService> _logger;

    private const string SessionCacheKeyPrefix = "pay-sess:";

    private static readonly HybridCacheOptions CacheOptions = HybridCacheOptions.DistributedOnly with
    {
        L2Duration = TimeSpan.FromMinutes(5)
    };

    public StripePaymentSessionCacheService(
        IHybridCache cache,
        ILogger<StripePaymentSessionCacheService> logger)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<SessionValidationResult?> GetAsync(string sessionId, CancellationToken ct = default)
    {
        var cacheKey = BuildCacheKey(sessionId);

        try
        {
            var cached = await _cache.GetAsync<SessionCacheData>(cacheKey, ct);

            if (cached is null)
            {
                return null;
            }

            return new SessionValidationResult(cached.OrderId, cached.UserId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get payment session {SessionId} from cache", sessionId);
            return null;
        }
    }

    public async Task SetAsync(string sessionId, Guid orderId, string userId, CancellationToken ct = default)
    {
        var cacheKey = BuildCacheKey(sessionId);
        var cacheData = new SessionCacheData(orderId, userId);

        try
        {
            await _cache.SetAsync(cacheKey, cacheData, CacheOptions, ct);
            _logger.LogDebug("Cached payment session {SessionId}", sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cache payment session {SessionId}", sessionId);
        }
    }

    public async Task RemoveAsync(string sessionId, CancellationToken ct = default)
    {
        var cacheKey = BuildCacheKey(sessionId);

        try
        {
            await _cache.RemoveAsync(cacheKey, ct);
            _logger.LogDebug("Removed payment session {SessionId} from cache", sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove payment session {SessionId} from cache", sessionId);
        }
    }

    public bool ValidateOwnership(SessionValidationResult cached, string userId)
    {
        // For this platform, we'll assume UserId can be compared directly.
        // Guest user logic would be handled by the caller or a constant if needed.
        return cached.UserId == userId;
    }

    private static string BuildCacheKey(string sessionId) =>
        $"{SessionCacheKeyPrefix}{sessionId}";

    /// <summary>
    /// Internal cache data structure for payment session.
    /// </summary>
    private sealed record SessionCacheData(Guid OrderId, string UserId);
}
