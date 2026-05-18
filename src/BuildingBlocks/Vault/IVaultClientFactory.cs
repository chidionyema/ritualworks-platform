using Haworks.BuildingBlocks.Vault.Options;
using VaultSharp;

namespace Haworks.BuildingBlocks.Vault;

/// <summary>
/// Builds an authenticated <see cref="IVaultClient"/> and reports back how
/// long the client's token is valid for, so callers can rebuild it before
/// the token expires.
/// </summary>
public interface IVaultClientFactory
{
    Task<VaultClientHandle> CreateClientAsync(VaultOptions options, CancellationToken ct);
}

/// <summary>
/// Authenticated VaultClient + the lease info from the underlying AppRole
/// login. Consumers compare <see cref="CreatedAt"/> + <see cref="LeaseDuration"/>
/// against now to decide when to rebuild.
/// </summary>
public sealed record VaultClientHandle(IVaultClient Client, DateTime CreatedAt, TimeSpan LeaseDuration, HttpClient? HttpClient = null) : IDisposable
{
    public void Dispose() => HttpClient?.Dispose();
}
