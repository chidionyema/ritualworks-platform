using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Haworks.FeatureFlags.Api.Domain;
using Haworks.FeatureFlags.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Haworks.FeatureFlags.Api.Application;

public interface IFeatureFlagCache
{
    bool Evaluate(string flagName, string userId, string region);
    void Update(string flagName, bool isEnabled, List<FeatureFlagRule> rules);
    Task WarmupAsync(CancellationToken ct);
}

public class FeatureFlagCache : IFeatureFlagCache
{
    private readonly ConcurrentDictionary<string, CachedFlag> _cache = new();
    private readonly IServiceProvider _serviceProvider;

    public FeatureFlagCache(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public async Task WarmupAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FeatureFlagsDbContext>();
        
        var flags = await db.FeatureFlags.Include(x => x.Rules).ToListAsync(ct);
        foreach (var flag in flags)
        {
            Update(flag.Name, flag.IsEnabled, flag.Rules);
        }
    }

    public void Update(string flagName, bool isEnabled, List<FeatureFlagRule> rules)
    {
        _cache[flagName] = new CachedFlag(isEnabled, rules);
    }

    public bool Evaluate(string flagName, string userId, string region)
    {
        if (!_cache.TryGetValue(flagName, out var flag))
        {
            FeatureFlagMetrics.CacheMisses.Add(1);
            return false;
        }

        FeatureFlagMetrics.CacheHits.Add(1);
        if (!flag.IsEnabled) return false;

        foreach (var rule in flag.Rules)
        {
            // AND logic: all non-null conditions on the rule must match.
            if (rule.UserId != null && rule.UserId != userId) continue;
            if (rule.Region != null && rule.Region != region) continue;

            // Identity/region conditions passed (or were not set); check percentage last.
            if (rule.PercentageRollout.HasValue)
            {
                // Use a deterministic, stable hash — GetHashCode() is process-scoped and non-deterministic.
                // Mask sign bit instead of Math.Abs to avoid overflow on int.MinValue.
                var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(userId));
                var hash = BitConverter.ToInt32(hashBytes, 0) & 0x7FFFFFFF;
                if (hash % 100 >= rule.PercentageRollout.Value) continue;
            }

            return true;
        }

        return false;
    }

    private sealed record CachedFlag(bool IsEnabled, List<FeatureFlagRule> Rules);
}
