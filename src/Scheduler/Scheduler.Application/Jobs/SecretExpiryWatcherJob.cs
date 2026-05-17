using Haworks.Contracts.Secrets;
using MassTransit;
using Microsoft.Extensions.Logging;
using VaultSharp;
using VaultSharp.Core;

namespace Haworks.Scheduler.Application.Jobs;

/// <summary>
/// Hangfire recurring job (every 15 minutes) that monitors KV v2 secret ages
/// and publishes <see cref="SecretExpiryWarningEvent"/> when a secret exceeds
/// its configured warning threshold (typically 80% of total TTL).
/// </summary>
public sealed class SecretExpiryWatcherJob
{
    private static readonly Dictionary<string, (TimeSpan TotalTtl, double WarnAt)> TrackedSecrets = new()
    {
        ["payments/stripe"]                   = (TimeSpan.FromDays(90), 0.80),
        ["identity/jwt"]                      = (TimeSpan.FromDays(30), 0.80),
        ["notifications/providers/sendgrid"]  = (TimeSpan.FromDays(365), 0.80),
        ["notifications/providers/twilio"]    = (TimeSpan.FromDays(365), 0.80),
        ["bff-web/hub"]                       = (TimeSpan.FromDays(365), 0.80),
    };

    private readonly IVaultClient _vaultClient;
    private readonly IPublishEndpoint _publishEndpoint;
    private readonly ILogger<SecretExpiryWatcherJob> _logger;

    public SecretExpiryWatcherJob(
        IVaultClient vaultClient,
        IPublishEndpoint publishEndpoint,
        ILogger<SecretExpiryWatcherJob> logger)
    {
        _vaultClient = vaultClient;
        _publishEndpoint = publishEndpoint;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _logger.LogDebug("SecretExpiryWatcherJob starting cycle");

        foreach (var (path, (totalTtl, warnAt)) in TrackedSecrets)
        {
            if (ct.IsCancellationRequested)
                break;

            try
            {
                await CheckSecretAgeAsync(path, totalTtl, warnAt, ct).ConfigureAwait(false);
            }
            catch (VaultApiException ex) when (
                ex.HttpStatusCode == System.Net.HttpStatusCode.ServiceUnavailable ||
                ex.HttpStatusCode == System.Net.HttpStatusCode.InternalServerError)
            {
                _logger.LogWarning(
                    "Vault unavailable ({StatusCode}) while checking {Path}; skipping",
                    ex.HttpStatusCode, path);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "Error checking secret age for {Path}; skipping", path);
            }
        }

        _logger.LogDebug("SecretExpiryWatcherJob cycle complete");
    }

    private async Task CheckSecretAgeAsync(
        string path, TimeSpan totalTtl, double warnAt, CancellationToken ct)
    {
        var metadata = await _vaultClient.V1.Secrets.KeyValue.V2
            .ReadSecretMetadataAsync(path, mountPoint: "secret")
            .ConfigureAwait(false);

        // VaultSharp returns CreatedTime as a string in ISO 8601 format
        var createdTimeStr = metadata.Data.CreatedTime;
        var createdTime = DateTimeOffset.Parse(createdTimeStr, System.Globalization.CultureInfo.InvariantCulture);
        var age = DateTimeOffset.UtcNow - createdTime;
        var agePercent = age / totalTtl;

        if (agePercent >= warnAt)
        {
            _logger.LogWarning(
                "Secret {Path} is at {AgePercent:P0} of its TTL ({Age:g} / {TotalTtl:g})",
                path, agePercent, age, totalTtl);

            await _publishEndpoint.Publish(new SecretExpiryWarningEvent
            {
                SecretPath = $"secret/data/{path}",
                AgePercent = agePercent,
                LastRotatedAt = createdTime
            }, ct).ConfigureAwait(false);
        }
        else
        {
            _logger.LogDebug(
                "Secret {Path} is at {AgePercent:P0} of its TTL — healthy",
                path, agePercent);
        }
    }
}
