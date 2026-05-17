using Hangfire;
using Microsoft.Extensions.Logging;
using VaultSharp;

namespace Haworks.Scheduler.Application.Jobs;

/// <summary>
/// One-off Hangfire job scheduled by <see cref="RotateJwtKeyJob"/> after the
/// overlap window elapses. Deletes the previous JWT key from Vault KV v2.
/// </summary>
public sealed class ClearPreviousJwtKeyJob
{
    private const string VaultMountPoint = "secret";
    private const string JwtPreviousKeyPath = "identity/jwt-previous";

    private readonly IVaultClient _vaultClient;
    private readonly ILogger<ClearPreviousJwtKeyJob> _logger;

    public ClearPreviousJwtKeyJob(
        IVaultClient vaultClient,
        ILogger<ClearPreviousJwtKeyJob> logger)
    {
        _vaultClient = vaultClient;
        _logger = logger;
    }

    [AutomaticRetry(Attempts = 2)]
    public async Task RunAsync(CancellationToken ct)
    {
        _logger.LogInformation("Clearing previous JWT key from Vault (overlap window expired)");

        try
        {
            await _vaultClient.V1.Secrets.KeyValue.V2
                .DeleteSecretAsync(JwtPreviousKeyPath, mountPoint: VaultMountPoint)
                .ConfigureAwait(false);

            _logger.LogInformation("Previous JWT key deleted from Vault successfully");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete previous JWT key from Vault; secret may already be gone");
        }
    }
}
