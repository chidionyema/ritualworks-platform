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
    private static readonly (string VaultPath, string ConfigPrefix)[] s_kvMappings =
    [
        ("stripe",          "PaymentProviders:Stripe"),
        ("jwt",             "Jwt"),
        ("hub",             "HubSecurity"),
        ("oauth/google",    "Authentication:Google"),
        ("oauth/microsoft", "Authentication:Microsoft"),
        ("oauth/facebook",  "Authentication:Facebook"),
    ];

    public static async Task<IReadOnlyDictionary<string, string?>> LoadAsync(
        IConfiguration configuration,
        ILogger? logger = null,
        IVaultAppRoleAuthenticator? authenticator = null,
        CancellationToken ct = default)
    {
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
        foreach (var (vaultPath, configPrefix) in s_kvMappings)
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
