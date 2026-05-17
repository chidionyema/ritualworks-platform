namespace Haworks.BuildingBlocks.Vault;

/// <summary>
/// Provides database credentials from Vault static roles with caching.
/// </summary>
public interface IVaultCredentialProvider
{
    /// <summary>
    /// Gets the current database credentials for the specified Vault static role.
    /// Returns cached credentials if within the rotation period; fetches fresh
    /// credentials from Vault if expired or near expiry.
    /// </summary>
    Task<(string Username, string Password)> GetDatabaseCredentialsAsync(
        string roleName, CancellationToken ct = default);
}
