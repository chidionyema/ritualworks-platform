using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Haworks.BuildingBlocks.Vault;

/// <summary>
/// Hooks <see cref="IHostApplicationLifetime.ApplicationStopping"/> and
/// revokes the service's Vault token via auth/token/revoke-self before
/// the host fully exits. Reduces blast radius if the host process state
/// is captured (memory dump, swapped page, etc.) after shutdown but
/// before the token's natural TTL expires.
///
/// Registered automatically by <see cref="VaultServiceCollectionExtensions.AddVaultIntegration"/>.
/// Failures during revoke are logged but never propagated — shutdown is
/// in flight and there's nothing useful to do on failure (the token
/// expires naturally at its TTL regardless).
/// </summary>
internal sealed class VaultTokenRevocationHostedService(
    IVaultService vault,
    IHostApplicationLifetime lifetime,
    ILogger<VaultTokenRevocationHostedService> logger
) : IHostedService
{
    private CancellationTokenRegistration _registration;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _registration = lifetime.ApplicationStopping.Register(OnStopping);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _registration.Dispose();
        return Task.CompletedTask;
    }

    private void OnStopping()
    {
        // ApplicationStopping fires synchronously during shutdown — block on
        // the async revoke with a tight timeout so we don't hang the
        // shutdown sequence if Vault is unreachable.
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            vault.RevokeTokenAsync(cts.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "[VaultTokenRevocation] revoke-self timed out or failed during shutdown");
        }
    }
}
