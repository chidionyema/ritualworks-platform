using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Haworks.BuildingBlocks.Startup;

public sealed class StartupReadinessHealthCheck : IHealthCheck
{
    private readonly StartupTaskRunner _runner;
    public StartupReadinessHealthCheck(StartupTaskRunner runner) => _runner = runner;

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
        => Task.FromResult(_runner.IsReady
            ? HealthCheckResult.Healthy("All startup tasks completed")
            : HealthCheckResult.Degraded("Startup tasks still running"));
}
