#nullable enable
using Haworks.Identity.Application;
using Haworks.Identity.Domain;
using Microsoft.EntityFrameworkCore;

namespace Haworks.Identity.Infrastructure;

/// <summary>
/// Service for managing token revocation with hybrid caching.
/// Uses L1 (memory) + L2 (distributed) cache to ensure revoked tokens
/// are recognized across all application instances.
/// </summary>
public class TokenRevocationService : ITokenRevocationService
{
    private readonly AppIdentityDbContext _context;
    private readonly IHybridCache _cache;
    private readonly ILogger<TokenRevocationService> _logger;

    private const string RevokedTokenCachePrefix = "revoked_token";

    public TokenRevocationService(
        AppIdentityDbContext context,
        IHybridCache cache,
        ILogger<TokenRevocationService> logger)
    {
        _context = context;
        _cache = cache;
        _logger = logger;
    }

    public async Task RevokeTokenAsync(string tokenValue, string userId, DateTime expiryDate, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(tokenValue) || string.IsNullOrEmpty(userId))
        {
            _logger.LogWarning("Revoke token called with missing token or userId");
            return;
        }

        if (await IsTokenRevokedAsync(tokenValue, ct))
        {
            return; // Already revoked
        }

        var revokedToken = RevokedToken.Create(tokenValue, expiryDate, "Manual revocation", userId);
        _context.RevokedTokens.Add(revokedToken);
        await _context.SaveChangesAsync(ct);

        // Cache the revocation with TTL matching token expiry
        await CacheRevocationAsync(tokenValue, expiryDate, ct);

        _logger.LogInformation("Token revoked for user {UserId}", userId);
    }

    public async Task<bool> IsTokenRevokedAsync(string tokenValue, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(tokenValue))
        {
            return false;
        }

        var cacheKey = BuildCacheKey(tokenValue);

        // Check hybrid cache (L1 → L2)
        var cached = await _cache.GetAsync<RevokedTokenMarker>(cacheKey, ct);
        if (cached is not null)
        {
            return true;
        }

        // Check database (slow path) — existence check only, no need to load the full entity
        var isRevoked = await _context.RevokedTokens
            .AnyAsync(rt => rt.Token == tokenValue, ct);

        if (isRevoked)
        {
            // Cache the revocation for future checks — use a reasonable TTL since we don't have the expiry
            var expiry = await _context.RevokedTokens
                .Where(rt => rt.Token == tokenValue)
                .Select(rt => rt.ExpiresAt)
                .FirstOrDefaultAsync(ct);
            await CacheRevocationAsync(tokenValue, expiry, ct);
            return true;
        }

        return false;
    }

    private async Task CacheRevocationAsync(string tokenValue, DateTime expiryDate, CancellationToken ct)
    {
        var cacheKey = BuildCacheKey(tokenValue);
        var ttl = expiryDate > DateTime.UtcNow
            ? expiryDate - DateTime.UtcNow
            : TimeSpan.FromMinutes(5); // Minimum cache for recently expired tokens

        var options = new HybridCacheOptions
        {
            L1Duration = ttl,
            L2Duration = ttl
        };

        await _cache.SetAsync(cacheKey, RevokedTokenMarker.Instance, options, ct);
    }

    private static string BuildCacheKey(string tokenValue) =>
        $"{RevokedTokenCachePrefix}:{tokenValue}";

    /// <summary>
    /// Marker record for revoked tokens in cache.
    /// Uses singleton pattern since we only need to check existence.
    /// </summary>
    private sealed record RevokedTokenMarker
    {
        public static readonly RevokedTokenMarker Instance = new();
        public bool IsRevoked => true;
    }
}
