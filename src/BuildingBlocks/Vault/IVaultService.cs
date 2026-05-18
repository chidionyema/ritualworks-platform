namespace Haworks.BuildingBlocks.Vault;

/// <summary>
/// Service for interacting with HashiCorp Vault for secrets management.
/// Manages database credential lifecycle with automatic renewal.
/// </summary>
public interface IVaultService : IDisposable
{
    /// <summary>
    /// Initializes the Vault connection and retrieves initial credentials.
    /// Must be called before using other methods.
    /// </summary>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets database credentials for the named Vault database role,
    /// refreshing from Vault if expired or near expiry. Each role has its
    /// own credential store inside this service so per-bounded-context
    /// dynamic users (haworks-catalog, haworks-orders, ...) are issued and
    /// renewed independently.
    /// </summary>
    Task<(string Username, string Password)> GetDatabaseCredentialsAsync(string roleName, CancellationToken ct = default);

    /// <summary>
    /// Forces a credential refresh for the named role from Vault.
    /// </summary>
    Task RefreshCredentials(string roleName, CancellationToken ct = default);

    /// <summary>
    /// Gets a connection string for the named role using its current credentials.
    /// </summary>
    Task<string> GetDatabaseConnectionStringAsync(string roleName, CancellationToken ct = default);

    /// <summary>
    /// Starts the background credential renewal loop.
    /// </summary>
    Task StartCredentialRenewalAsync(CancellationToken stoppingToken);

    /// <summary>
    /// Lease expiry for the named role's currently-cached credential.
    /// </summary>
    DateTime LeaseExpiryFor(string roleName);

    /// <summary>
    /// Lease duration for the named role's currently-cached credential.
    /// </summary>
    TimeSpan LeaseDurationFor(string roleName);

    /// <summary>
    /// Reads a secret from the Vault KV v2 secrets engine.
    /// </summary>
    /// <param name="path">The path to the secret (e.g., "stripe", "jwt", "minio").</param>
    /// <param name="key">The key within the secret data (e.g., "secret_key", "webhook_secret").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The secret value, or null if not found.</returns>
    Task<string?> GetKvSecretAsync(string path, string key, CancellationToken ct = default);

    /// <summary>
    /// Gets information about the current Vault token.
    /// </summary>
    Task<VaultTokenInfo> GetTokenInfoAsync(CancellationToken ct = default);

    /// <summary>
    /// Revokes the current Vault token via auth/token/revoke-self. Reduces
    /// blast radius if a host is compromised after shutdown but before the
    /// token's natural TTL expires. Safe no-op if no client has been built
    /// yet (nothing to revoke). Failures are swallowed + logged because
    /// shutdown is in flight and there's nothing useful to do on failure.
    /// </summary>
    Task RevokeTokenAsync(CancellationToken ct = default);
}
