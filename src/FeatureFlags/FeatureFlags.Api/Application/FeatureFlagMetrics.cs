using System.Diagnostics.Metrics;

namespace Haworks.FeatureFlags.Api.Application;

public static class FeatureFlagMetrics
{
    public const string MeterName = "Haworks.FeatureFlags";
    private static readonly Meter Meter = new(MeterName);

    public static readonly Counter<long> CacheHits = Meter.CreateCounter<long>(
        "featureflags.cache.hits", 
        description: "Number of evaluations served from in-memory cache");

    public static readonly Counter<long> CacheMisses = Meter.CreateCounter<long>(
        "featureflags.cache.misses", 
        description: "Number of evaluations where the flag was not found in cache");
}
