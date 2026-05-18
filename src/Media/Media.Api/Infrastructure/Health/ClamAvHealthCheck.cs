using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using nClam;

namespace Haworks.Media.Api.Infrastructure.Health;

public sealed class ClamAvHealthCheck(IOptions<ClamAvOptions> opts, ILogger<ClamAvHealthCheck> logger) : IHealthCheck
{
    private readonly ClamAvOptions _opts = opts.Value;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            var clam = new ClamClient(_opts.Host, _opts.Port);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            var version = await clam.GetVersionAsync(cts.Token);
            return HealthCheckResult.Healthy($"ClamAV: {version}");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "An error occurred in {MethodName}", nameof(CheckHealthAsync));
            return HealthCheckResult.Unhealthy("ClamAV unreachable", ex);
        }
    }
}
