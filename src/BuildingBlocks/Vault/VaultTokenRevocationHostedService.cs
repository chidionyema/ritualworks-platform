using Microsoft.Extensions.DependencyInjection;
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
///
/// **Lazy <see cref="IVaultService"/> resolution.** The service is
/// resolved via <see cref="IServiceProvider"/> at shutdown, NOT injected
/// in the constructor. Constructor injection would force eager
/// <see cref="VaultService"/> construction at host start — which fails
/// in environments where Vault is configured-but-not-bootable (e.g.
/// integration tests with <c>Vault:Enabled=true</c> but no
/// <c>Vault:Address</c>) or where AddVaultIntegration was called but
/// no other code path actually exercises Vault.
/// </summary>
internal sealed class VaultTokenRevocationHostedService(
    IServiceProvider services,
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
        // Resolve IVaultService lazily so this hosted service doesn't trigger
        // VaultService construction (and its config validation) at host start.
        // If the service couldn't be constructed (e.g. config invalid in tests),
        // GetService returns null OR throws — either way we cleanly skip the
        // revoke since there's nothing to revoke.
        IVaultService? vault;
        try
        {
            vault = services.GetService<IVaultService>();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex,
                "[VaultTokenRevocation] IVaultService not resolvable at shutdown; skipping revoke");
            return;
        }
        if (vault is null) return;

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
