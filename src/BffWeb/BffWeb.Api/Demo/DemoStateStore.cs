using System.Collections.Concurrent;
using System.Threading.RateLimiting;

namespace Haworks.BffWeb.Api.Demo;

public sealed record IdempotencyResult(Guid OrderId, string Status, DateTime ProcessedAt);

public sealed record IdempotencyEntry(IdempotencyResult Result, DateTime CreatedAt, DateTime ExpiresAt)
{
    public bool IsExpired(DateTime now) => ExpiresAt <= now;
}

/// <summary>
/// Singleton store for demo-session-scoped state — idempotency keys,
/// inventory versions, and per-session FixedWindowRateLimiters. Lives in
/// BffWeb because the demos are presentation-tier; the underlying patterns
/// are exercised against the real microservices in Phase 2.
/// </summary>
public sealed class DemoStateStore
{
    public ConcurrentDictionary<string, IdempotencyEntry> IdempotencyKeys { get; } = new();
    public ConcurrentDictionary<string, int> InventoryVersions { get; } = new();
    public ConcurrentDictionary<Guid, RateLimiter> RateLimiters { get; } = new();

    public RateLimiter GetOrCreateLimiter(Guid sessionId, int limit, int windowSeconds)
    {
        return RateLimiters.GetOrAdd(sessionId, _ => new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
        {
            PermitLimit = limit,
            Window = TimeSpan.FromSeconds(windowSeconds),
            QueueLimit = 0,
        }));
    }
}
