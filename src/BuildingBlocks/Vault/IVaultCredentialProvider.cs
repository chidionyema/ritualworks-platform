namespace Haworks.BuildingBlocks.Vault;

/// <summary>
/// Provides database credentials from Vault with caching and lease tracking.
/// </summary>
public interface IVaultCredentialProvider
{
    /// <summary>
    /// Fetches database credentials for the given Vault static role.
    /// Results are cached for ~90% of the rotation period.
    /// </summary>
    Task<(string Username, string Password)> GetDatabaseCredentialsAsync(
        string roleName, CancellationToken ct = default);

    /// <summary>
    /// Returns the current lease status without making a Vault call.
    /// </summary>
    VaultLeaseStatus GetLeaseStatus();
}

/// <summary>
/// Represents the current status of the Vault credential lease/cache.
/// </summary>
public sealed record VaultLeaseStatus
{
    /// <summary>When the cached credentials expire.</summary>
    public required DateTimeOffset CachedUntil { get; init; }

    /// <summary>When the credentials were last fetched.</summary>
    public required DateTimeOffset FetchedAt { get; init; }

    /// <summary>Percentage of TTL elapsed (0.0 = just fetched, 1.0+ = expired).</summary>
    public required double TtlPercentElapsed { get; init; }

    /// <summary>Whether credentials have expired.</summary>
    public bool IsExpired => DateTimeOffset.UtcNow >= CachedUntil;

    /// <summary>Whether credentials have ever been fetched.</summary>
    public required bool HasCredentials { get; init; }
}
