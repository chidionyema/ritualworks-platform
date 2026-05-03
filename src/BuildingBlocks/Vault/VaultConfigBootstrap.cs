using VaultSharp;
using VaultSharp.V1.AuthMethods.Token;

namespace Haworks.BuildingBlocks.Vault;

/// <summary>
/// One-shot Vault read at startup: AppRole-authenticates, pulls KV secrets,
/// and returns them flattened into a dictionary suitable for
/// <see cref="Microsoft.Extensions.Configuration.MemoryConfigurationBuilderExtensions"/>.
///
/// This runs *before* DI is built so handlers/options/factories that resolve
/// <see cref="Microsoft.Extensions.Configuration.IConfiguration"/> (JWT key,
/// OAuth client IDs, Stripe keys) see Vault values from the start.
///
/// Uses <see cref="VaultAppRoleAuthenticator"/> directly (the same
/// implementation registered in DI for runtime auth) so the two AppRole login
/// paths never drift.
/// </summary>
public static class VaultConfigBootstrap
{
    /// <summary>
    /// Maps a Vault KV path (under the "secret" mount) to a configuration
    /// section prefix. Each service supplies its own list — there is NO
    /// shared mapping here because in a polyrepo world that would create
    /// the same "everyone reads everything" coupling we are escaping.
    ///
    /// Example for identity-svc:
    ///   new KvMapping("identity/jwt",            "Jwt"),
    ///   new KvMapping("identity/oauth/google",   "Authentication:Google"),
    ///   new KvMapping("identity/oauth/microsoft","Authentication:Microsoft"),
    ///   new KvMapping("identity/oauth/facebook", "Authentication:Facebook"),
    /// </summary>
    public sealed record KvMapping(string VaultPath, string ConfigPrefix);

    public static async Task<IReadOnlyDictionary<string, string?>> LoadAsync(
        IConfiguration configuration,
        IReadOnlyList<KvMapping> kvMappings,
        ILogger? logger = null,
        IVaultAppRoleAuthenticator? authenticator = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(kvMappings);
        if (kvMappings.Count == 0)
        {
            logger?.LogWarning("[VaultBootstrap] No KV mappings supplied — no secrets loaded.");
            return new Dictionary<string, string?>();
        }

        var address      = Required(configuration, "Vault:Address");
        var roleIdPath   = Required(configuration, "Vault:RoleIdPath");
        var secretIdPath = Required(configuration, "Vault:SecretIdPath");

        await WaitForFileAsync(roleIdPath,   TimeSpan.FromSeconds(60), ct);
        await WaitForFileAsync(secretIdPath, TimeSpan.FromSeconds(60), ct);

        var roleId   = (await File.ReadAllTextAsync(roleIdPath,   ct)).Trim();
        var secretId = (await File.ReadAllTextAsync(secretIdPath, ct)).Trim();

        // Bootstrap runs before DI; if the caller didn't pass an authenticator,
        // construct one directly. Logger is intentionally null here because we
        // already have a bootstrap logger threaded through, and emitting two
        // log lines for one login event clutters startup output.
        authenticator ??= new VaultAppRoleAuthenticator();
        var login = await authenticator.LoginAsync(address, roleId, secretId, ct);

        var client = new VaultClient(new VaultClientSettings(address, new TokenAuthMethodInfo(login.ClientToken)));

        var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (vaultPath, configPrefix) in kvMappings)
        {
            try
            {
                var resp = await client.V1.Secrets.KeyValue.V2.ReadSecretAsync(
                    path: vaultPath, mountPoint: "secret");
                foreach (var (key, value) in resp.Data.Data)
                {
                    dict[$"{configPrefix}:{key}"] = value?.ToString();
                }
                logger?.LogInformation("[VaultBootstrap] Loaded {Count} keys from secret/{Path} -> {Prefix}",
                    resp.Data.Data.Count, vaultPath, configPrefix);
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "[VaultBootstrap] Failed to read secret/{Path}", vaultPath);
                throw;  // fail fast — these secrets are required for the app to start
            }
        }

        logger?.LogInformation("[VaultBootstrap] Loaded {Count} total config entries from Vault", dict.Count);
        return dict;
    }

    private static string Required(IConfiguration cfg, string key)
        => cfg[key] ?? throw new InvalidOperationException(
            $"Vault bootstrap requires '{key}' in configuration.");

    private static async Task WaitForFileAsync(string path, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!File.Exists(path) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(500, ct).ConfigureAwait(false);
        }

        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"Vault AppRole credential file did not appear within {timeout}: {path}");
        }
    }
}
