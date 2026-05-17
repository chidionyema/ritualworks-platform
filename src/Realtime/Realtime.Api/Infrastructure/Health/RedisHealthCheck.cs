using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace Haworks.Realtime.Api.Infrastructure.Health;

public sealed class RedisHealthCheck : IHealthCheck
{
    private readonly IConnectionMultiplexer _redis;

    public RedisHealthCheck(IConnectionMultiplexer redis)
    {
        _redis = redis;
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = _redis.IsConnected
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("Redis not connected");
            return Task.FromResult(result);
        }
        catch (Exception ex)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(ex.Message));
        }
    }
}
