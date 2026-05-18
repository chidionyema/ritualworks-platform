using Haworks.BuildingBlocks.Resilience;
using Haworks.BuildingBlocks.Vault.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Npgsql;
using System;
using System.Text.Json;

namespace Haworks.BuildingBlocks.Vault;

/// <summary>
/// DI registration helpers for the Vault integration. Call
/// <see cref="AddVaultIntegration"/> from a service's
/// <c>AddInfrastructure</c> to make <see cref="IVaultService"/> resolvable
/// — useful for any handler that needs runtime credential rotation
/// (e.g. identity-svc's vault-rotation demo endpoint).
///
/// Bootstraps:
///   - <see cref="VaultOptions"/> (from <c>"Vault"</c> config section)
///   - <see cref="DatabaseOptions"/> (from <c>"Database"</c> config section)
///   - <see cref="ICertificateValidator"/>, <see cref="ISecretFileReader"/>
///   - <see cref="IVaultClientFactory"/>
///   - <see cref="IResiliencePolicyFactory"/>
///   - <see cref="IVaultService"/>
///
/// Idempotent — safe to call multiple times; each registration uses
/// <c>TryAdd*</c> semantics where appropriate so callers that already
/// registered some pieces (e.g. <see cref="IVaultAppRoleAuthenticator"/>
/// in identity DI) aren't overridden.
/// </summary>
public static class VaultServiceCollectionExtensions
{
    private static readonly string[] VaultHealthCheckTags = ["vault", "ready"];
    public static IServiceCollection AddVaultIntegration(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<VaultOptions>()
            .Bind(configuration.GetSection(VaultOptions.SectionName))
            .ValidateOnStart();

        services.AddOptions<DatabaseOptions>()
            .Bind(configuration.GetSection(DatabaseOptions.SectionName))
            // Database:Host is optional. When set, VaultService can build
            // connection strings from dynamic credentials (vault-pg sandbox).
            // When absent (Neon / managed PG), services use the static
            // connection string from bootstrap.sh and the PeriodicPasswordProvider
            // gracefully skips rotation. See AddVaultNpgsqlDataSource().
            .PostConfigure(opts =>
            {
                if (opts.Port == 0) opts.Port = 5432;
            });

        // Vault wiring needs an HTTP client for the Vault API (the AppRole
        // authenticator uses IHttpClientFactory).
        services.AddHttpClient();

        services.AddSingleton<ICertificateValidator, CertificateValidator>();
        services.AddSingleton<ISecretFileReader, SecretFileReader>();
        services.AddSingleton<IVaultAppRoleAuthenticator, VaultAppRoleAuthenticator>();
        services.AddSingleton<IVaultClientFactory, VaultClientFactory>();

        services.AddSingleton<IResiliencePolicyFactory, ResiliencePolicyFactory>();

        services.AddSingleton<IVaultService, VaultService>();

        // Token revocation on graceful shutdown — security hardening per
        // .claude/rules/security.md. Hooks IHostApplicationLifetime.ApplicationStopping
        // and calls auth/token/revoke-self with a 3s timeout. Reduces blast
        // radius if the host process state is captured after shutdown but
        // before the token's natural TTL expires.
        services.AddHostedService<VaultTokenRevocationHostedService>();

        return services;
    }

    /// <summary>
    /// Registers an NpgsqlDataSource with production-grade pool defaults.
    /// When <c>Vault:DatabaseMode</c> is <c>StaticRole</c>, registers a
    /// <see cref="NpgsqlDataSourceBuilder.UsePeriodicPasswordProvider"/> that
    /// fetches rotated credentials from Vault's static database role endpoint.
    /// When <c>None</c> (default — Neon / managed PG), uses the static password
    /// from the connection string with no rotation.
    /// </summary>
    public static IServiceCollection AddVaultNpgsqlDataSource(
        this IServiceCollection services,
        string connectionString,
        string roleName)
    {
        services.AddSingleton(sp =>
        {
            var csb = new NpgsqlConnectionStringBuilder(connectionString);
            if (csb.MaxPoolSize == 100) csb.MaxPoolSize = 50;
            if (csb.MinPoolSize == 0) csb.MinPoolSize = 5;
            if (csb.ConnectionIdleLifetime == 300) csb.ConnectionIdleLifetime = 120;

            var config = sp.GetRequiredService<IConfiguration>();
            var dbMode = config.GetValue("Vault:DatabaseMode", "None");
            var builder = new NpgsqlDataSourceBuilder(csb.ConnectionString);

            if (string.Equals(dbMode, "StaticRole", StringComparison.OrdinalIgnoreCase))
            {
                var vault = sp.GetRequiredService<IVaultService>();
                var logger = sp.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("Haworks.Vault.PeriodicPasswordProvider");
                // F-01 fix: Track the last-known-good password from Vault so
                // that on failure we return the LAST SUCCESSFUL Vault password,
                // not the original static password (which Vault may have
                // already rotated away from).
                var lastKnownGoodPassword = csb.Password ?? string.Empty;

                builder.UsePeriodicPasswordProvider(async (sb, ct) =>
                {
                    try
                    {
                        var (_, password) = await vault.GetDatabaseCredentialsAsync(roleName, ct);
                        lastKnownGoodPassword = password;
                        return password;
                    }
                    catch (Exception ex)
                    {
                        VaultMetrics.CredentialRotationFailure.Add(1, new KeyValuePair<string, object?>("role", roleName), new KeyValuePair<string, object?>("error_type", ex.GetType().Name));
                        logger.LogWarning(ex,
                            "Vault credential rotation failed for role {Role}; returning last-known-good password",
                            roleName);
                        return lastKnownGoodPassword;
                    }
                }, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(30));
            }
            else if (string.Equals(dbMode, "AgentFile", StringComparison.OrdinalIgnoreCase))
            {
                var agentSecretsPath = config["Vault:Agent:SecretsPath"] ?? "/vault/secrets";
                var serviceName = roleName.StartsWith("haworks-", StringComparison.OrdinalIgnoreCase)
                    ? roleName["haworks-".Length..]
                    : roleName;
                var credFile = Path.Combine(agentSecretsPath, $"db-{serviceName}.json");
                var logger = sp.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("Haworks.Vault.AgentFilePasswordProvider");
                var fallbackPassword = csb.Password ?? string.Empty;
                var lastKnownGoodPassword = fallbackPassword;

                builder.UsePeriodicPasswordProvider(async (sb, ct) =>
                {
                    var result = await ReadAgentCredentialFileAsync(credFile, logger, ct).ConfigureAwait(false);

                    if (result is null)
                    {
                        logger.LogDebug("Agent credential file {File} not found; using fallback", credFile);
                        return lastKnownGoodPassword;
                    }

                    if (result.Value.Error is { } ex)
                    {
                        VaultMetrics.CredentialRotationFailure.Add(1,
                            new KeyValuePair<string, object?>("role", serviceName),
                            new KeyValuePair<string, object?>("error_type", ex.GetType().Name));
                        logger.LogWarning(ex,
                            "Failed to read agent credential file {File}; using last-known-good password",
                            credFile);
                        return lastKnownGoodPassword;
                    }

                    if (!string.IsNullOrEmpty(result.Value.Username))
                        sb.Username = result.Value.Username;

                    lastKnownGoodPassword = result.Value.Password;
                    return result.Value.Password;
                }, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(10));
            }

            return builder.Build();
        });
        return services;
    }

    /// <summary>
    /// Reads a Vault Agent-rendered credential JSON file containing
    /// <c>{ "username": "...", "password": "..." }</c>.
    /// Returns <c>null</c> when the file does not exist, or an error result
    /// when the file is malformed. Extracted for unit-testability.
    /// </summary>
    internal static async ValueTask<AgentCredentialResult?> ReadAgentCredentialFileAsync(
        string filePath, ILogger? logger = null, CancellationToken ct = default)
    {
        if (!File.Exists(filePath))
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json))
                return new AgentCredentialResult(null, string.Empty,
                    new InvalidOperationException($"Agent credential file {filePath} is empty (0 bytes) — Vault Agent may still be rendering"));
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var password = root.GetProperty("password").GetString()
                ?? throw new InvalidOperationException($"Agent credential file {filePath} has null password");

            string? username = null;
            if (root.TryGetProperty("username", out var usernameEl))
                username = usernameEl.GetString();

            return new AgentCredentialResult(username, password, Error: null);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to parse agent credential file {File}", filePath);
            return new AgentCredentialResult(null, string.Empty, ex);
        }
    }

    /// <summary>Result of reading a Vault Agent credential file.</summary>
    internal readonly record struct AgentCredentialResult(
        string? Username,
        string Password,
        Exception? Error);
}
