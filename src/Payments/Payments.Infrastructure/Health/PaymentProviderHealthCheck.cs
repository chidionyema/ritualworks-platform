using Haworks.Payments.Application.Interfaces;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace Haworks.Payments.Infrastructure.Health;

/// <summary>
/// Health check for the active payment provider.
/// Verifies API connectivity and credential validity.
/// </summary>
internal sealed class PaymentProviderHealthCheck(
    IPaymentGateway gateway,
    ILogger<PaymentProviderHealthCheck> logger) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            var status = await gateway.CheckHealthAsync(cancellationToken);
            sw.Stop();

            var data = new Dictionary<string, object>
            {
                ["provider"] = gateway.ActiveProvider.ToString(),
                ["responseTimeMs"] = sw.ElapsedMilliseconds
            };

            if (status.IsHealthy)
            {
                logger.LogDebug(
                    "Payment provider {Provider} health check passed in {ElapsedMs}ms",
                    gateway.ActiveProvider,
                    sw.ElapsedMilliseconds);

                return HealthCheckResult.Healthy(
                    $"{gateway.ActiveProvider} is responsive",
                    data);
            }
            
            logger.LogWarning(
                "Payment provider {Provider} health check degraded: {Message}",
                gateway.ActiveProvider,
                status.Message);

            return HealthCheckResult.Degraded(
                $"{gateway.ActiveProvider}: {status.Message}",
                data: data);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Payment provider {Provider} health check failed with exception",
                gateway.ActiveProvider);

            return HealthCheckResult.Unhealthy(
                $"{gateway.ActiveProvider} unreachable: {ex.Message}",
                ex,
                new Dictionary<string, object>
                {
                    ["provider"] = gateway.ActiveProvider.ToString()
                });
        }
    }
}
