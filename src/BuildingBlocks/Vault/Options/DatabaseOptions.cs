using System.ComponentModel.DataAnnotations;

namespace Haworks.BuildingBlocks.Vault.Options;

/// <summary>
/// Configuration options for database connection via Vault credentials.
/// Bound from appsettings.json "Database" section.
/// </summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>
    /// Vault-managed database host. Optional — when blank (Neon / managed PG),
    /// services use the static connection string from bootstrap.sh and
    /// PeriodicPasswordProvider is not registered.
    /// </summary>
    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 5432;

    /// <summary>
    /// Postgres database name used by VaultService for ad-hoc connections.
    /// Per-context DbContexts use their own bounded-context DB name.
    /// </summary>
    public string Database { get; set; } = "postgres";

    /// <summary>
    /// Npgsql SslMode. "Disable" for local dev (postgres container has no TLS),
    /// "Require" or "VerifyFull" for prod.
    /// </summary>
    public string SslMode { get; set; } = "Disable";
}
