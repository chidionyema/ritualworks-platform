using Haworks.BuildingBlocks.Resilience;
using Haworks.BuildingBlocks.Vault.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
}
