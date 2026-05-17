namespace Haworks.BuildingBlocks.Vault;

/// <summary>
/// Provides a rotating connection string for database access.
/// Implementations update the connection string when Vault rotates credentials.
/// </summary>
public interface IConnectionStringProvider
{
    /// <summary>
    /// Gets the current connection string with the latest credentials.
    /// </summary>
    string GetConnectionString();
}
