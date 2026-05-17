using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace Haworks.BuildingBlocks.Vault;

/// <summary>
/// Health check that verifies Vault credentials are valid and not approaching expiry.
/// Returns Healthy if credentials are fresh, Degraded if within 90% of TTL,
/// and Unhealthy if credentials are expired or unavailable.
/// </summary>
public sealed class VaultLeaseHealthCheck : IHealthCheck
{
    private readonly IVaultCredentialProvider _credentialProvider;
    private readonly string _roleName;
    private readonly ILogger<VaultLeaseHealthCheck> _logger;

    public VaultLeaseHealthCheck(
        IVaultCredentialProvider credentialProvider,
        string roleName,
        ILogger<VaultLeaseHealthCheck> logger)
    {
        _credentialProvider = credentialProvider;
        _roleName = roleName;
        _logger = logger;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var status = _credentialProvider.GetLeaseStatus();

            if (!status.HasCredentials)
            {
                // No credentials fetched yet — attempt a fetch
                await _credentialProvider.GetDatabaseCredentialsAsync(_roleName, cancellationToken)
                    .ConfigureAwait(false);

                return HealthCheckResult.Healthy(
                    $"Vault credentials for role '{_roleName}' fetched successfully on first check.");
            }

            if (status.IsExpired)
            {
                _logger.LogWarning(
                    "Vault lease for role {RoleName} has expired. CachedUntil={CachedUntil:O}",
                    _roleName, status.CachedUntil);

                return HealthCheckResult.Unhealthy(
                    $"Vault credentials for role '{_roleName}' have expired.",
                    data: new Dictionary<string, object>
                    {
                        ["role"] = _roleName,
                        ["cachedUntil"] = status.CachedUntil.ToString("O"),
                        ["ttlPercent"] = status.TtlPercentElapsed
                    });
            }

            if (status.TtlPercentElapsed >= 0.9)
            {
                _logger.LogWarning(
                    "Vault lease for role {RoleName} is at {Percent:P0} of TTL. CachedUntil={CachedUntil:O}",
                    _roleName, status.TtlPercentElapsed, status.CachedUntil);

                return HealthCheckResult.Degraded(
                    $"Vault credentials for role '{_roleName}' are at {status.TtlPercentElapsed:P0} of TTL.",
                    data: new Dictionary<string, object>
                    {
                        ["role"] = _roleName,
                        ["cachedUntil"] = status.CachedUntil.ToString("O"),
                        ["ttlPercent"] = status.TtlPercentElapsed
                    });
            }

            return HealthCheckResult.Healthy(
                $"Vault credentials for role '{_roleName}' are valid.",
                data: new Dictionary<string, object>
                {
                    ["role"] = _roleName,
                    ["cachedUntil"] = status.CachedUntil.ToString("O"),
                    ["ttlPercent"] = status.TtlPercentElapsed
                });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Vault health check failed for role {RoleName}", _roleName);
            return HealthCheckResult.Unhealthy(
                $"Vault health check failed for role '{_roleName}': {ex.Message}",
                exception: ex);
        }
    }
}
