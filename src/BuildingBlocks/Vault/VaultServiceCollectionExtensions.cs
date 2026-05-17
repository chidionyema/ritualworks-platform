using Haworks.BuildingBlocks.Resilience;
using Haworks.BuildingBlocks.Vault.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Npgsql;
using System;

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
///   - <see cref="ICredentialStore"/> as transient + a
///     <c>Func&lt;ICredentialStore&gt;</c> factory the VaultService caches
///     per AppRole role-name internally.
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
            // VaultService.ValidateConfiguration() throws on a missing
            // Database.Host even when the dynamic DB credential path isn't
            // exercised. PostConfigure with sensible defaults so callers
            // that only need RefreshCredentials() (no dynamic DB role)
            // don't have to populate the section.
            .PostConfigure(opts =>
            {
                if (string.IsNullOrWhiteSpace(opts.Host)) opts.Host = "localhost";
                if (opts.Port == 0) opts.Port = 5432;
                if (string.IsNullOrWhiteSpace(opts.Database)) opts.Database = "postgres";
            })
            .ValidateOnStart();

        // Vault wiring needs an HTTP client for the Vault API (the AppRole
        // authenticator uses IHttpClientFactory).
        services.AddHttpClient();

        services.AddSingleton<ICertificateValidator, CertificateValidator>();
        services.AddSingleton<ISecretFileReader, SecretFileReader>();
        services.AddSingleton<IVaultAppRoleAuthenticator, VaultAppRoleAuthenticator>();
        services.AddSingleton<IVaultClientFactory, VaultClientFactory>();

        // VaultService caches one ICredentialStore per AppRole role-name
        // and refreshes its credentials in-place; each role gets a fresh
        // store on first use, so the registration is transient + a
        // factory delegate the consumer invokes per role.
        services.AddTransient<ICredentialStore, CredentialStore>();
        services.AddSingleton<Func<ICredentialStore>>(sp => sp.GetRequiredService<ICredentialStore>);

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
    /// Registers an NpgsqlDataSource that automatically rotates its password
    /// using Vault static roles.
    /// </summary>
    public static IServiceCollection AddVaultNpgsqlDataSource(
        this IServiceCollection services,
        string connectionString,
        string roleName)
    {
        services.AddSingleton(sp =>
        {
            var vault = sp.GetRequiredService<IVaultService>();
            var builder = new NpgsqlDataSourceBuilder(connectionString);
            builder.UsePeriodicPasswordProvider(async (sb, ct) =>
            {
                var (_, securePass) = await vault.GetDatabaseCredentialsAsync(roleName, ct);
                var pass = new System.Net.NetworkCredential(string.Empty, securePass).Password;
                return pass;
            }, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(30));
            return builder.Build();
        });
        return services;
    }

    /// <summary>
    /// Registers VaultCredentialProvider + VaultRotatingConnectionStringProvider
    /// for zero-downtime Postgres credential rotation via Vault static roles.
    /// The <see cref="IConnectionStringProvider"/> can be injected into DbContext
    /// factories for dynamic connection string resolution.
    /// </summary>
    public static IServiceCollection AddVaultRotatingPostgres(
        this IServiceCollection services,
        string roleName,
        IConfiguration configuration)
    {
        // Register IVaultCredentialProvider as singleton (it has internal caching).
        // It resolves IVaultService (already registered by AddVaultIntegration) to
        // obtain a VaultSharp IVaultClient via the existing factory infrastructure.
        services.AddSingleton<IVaultCredentialProvider>(sp =>
        {
            var factory = sp.GetRequiredService<IVaultClientFactory>();
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Options.VaultOptions>>().Value;
            // Create the client handle synchronously during DI build — token is short-lived but
            // the VaultCredentialProvider re-fetches via the static-creds endpoint which is
            // authenticated by the client's token.
            var handle = factory.CreateClientAsync(options, CancellationToken.None).GetAwaiter().GetResult();
            var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>()
                .CreateLogger<VaultCredentialProvider>();
            return new VaultCredentialProvider(handle.Client, logger);
        });

        // The base connection string is the static one from config (without dynamic user/pass).
        // Resolve by stripping the "haworks-" prefix to match the ConnectionStrings key.
        var serviceKey = roleName.Replace("haworks-", string.Empty, StringComparison.Ordinal);
        var baseConnectionString = configuration.GetConnectionString(serviceKey)
            ?? configuration.GetConnectionString("DefaultConnection")
            ?? string.Empty;

        // Register the rotating provider as both IConnectionStringProvider and IHostedService.
        services.AddSingleton<VaultRotatingConnectionStringProvider>(sp =>
        {
            var credProvider = sp.GetRequiredService<IVaultCredentialProvider>();
            var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>()
                .CreateLogger<VaultRotatingConnectionStringProvider>();
            return new VaultRotatingConnectionStringProvider(credProvider, roleName, baseConnectionString, logger);
        });
        services.AddSingleton<IConnectionStringProvider>(sp =>
            sp.GetRequiredService<VaultRotatingConnectionStringProvider>());
        services.AddHostedService(sp =>
            sp.GetRequiredService<VaultRotatingConnectionStringProvider>());

        // Register Vault lease health check
        services.AddHealthChecks()
            .Add(new HealthCheckRegistration(
                $"vault-lease-{roleName}",
                sp => new VaultLeaseHealthCheck(
                    sp.GetRequiredService<IVaultCredentialProvider>(),
                    roleName,
                    sp.GetRequiredService<ILogger<VaultLeaseHealthCheck>>()),
                failureStatus: HealthStatus.Degraded,
                tags: VaultHealthCheckTags));

        return services;
    }
}
