using VaultSharp;
using VaultSharp.Core;
using VaultSharp.V1.AuthMethods.Token;
using VaultSharp.V1.Commons;

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
    public sealed record KvMapping(string VaultPath, string ConfigPrefix, bool Optional = false);

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

        var address = Required(configuration, "Vault:Address");

        // Two ways to supply AppRole creds, in order of preference:
        //
        // 1. Direct config (Vault:RoleId + Vault:SecretId) — staged as Fly
        //    secrets at bootstrap time. Eliminates any startup round-trip
        //    to vault for fetching creds. Restarts of identity become
        //    independent of vault availability.
        //
        // 2. Path-based (Vault:RoleIdPath + Vault:SecretIdPath) — legacy
        //    file-on-disk pattern. The shim writes the files at boot
        //    after fetching from vault. Kept for backwards compatibility
        //    with the old bootstrap-shim approach.
        //
        // Direct wins if both are present, so an operator can override a
        // path-based deployment without redeploying the image.
        var directRoleId   = configuration["Vault:RoleId"];
        var directSecretId = configuration["Vault:SecretId"];

        string roleId;
        string secretId;
        if (!string.IsNullOrWhiteSpace(directRoleId) && !string.IsNullOrWhiteSpace(directSecretId))
        {
            roleId = directRoleId.Trim();
            secretId = directSecretId.Trim();
            logger?.LogInformation("[VaultBootstrap] Using AppRole creds from config (Vault:RoleId/SecretId)");
        }
        else
        {
            var roleIdPath   = Required(configuration, "Vault:RoleIdPath");
            var secretIdPath = Required(configuration, "Vault:SecretIdPath");

            await WaitForFileAsync(roleIdPath,   TimeSpan.FromSeconds(60), ct);
            await WaitForFileAsync(secretIdPath, TimeSpan.FromSeconds(60), ct);

            roleId   = (await File.ReadAllTextAsync(roleIdPath,   ct)).Trim();
            secretId = (await File.ReadAllTextAsync(secretIdPath, ct)).Trim();
            logger?.LogInformation("[VaultBootstrap] Using AppRole creds from disk paths (legacy)");
        }

        // Response-unwrap support. ci-stage-vault-creds.sh issues secret_ids
        // with X-Vault-Wrap-TTL: 1800s (30min) and stages the WRAPPING TOKEN as
        // Vault:SecretId, plus Vault:SecretIdIsWrapped=true to opt in to
        // unwrap. A leaked CI log only exposes the wrapper, useless after
        // 5 minutes or one unwrap (whichever comes first).
        //
        // Unwrap is gated on the explicit flag rather than auto-detected by
        // shape (e.g. "starts with hvs.") because both raw secret_ids and
        // wrapping tokens can share that prefix in newer Vault versions, so
        // shape-detection would be brittle.
        if (configuration.GetValue<bool>("Vault:SecretIdIsWrapped"))
        {
            try
            {
                secretId = await UnwrapSecretIdAsync(address, secretId, ct);
                logger?.LogInformation("[VaultBootstrap] Unwrapped secret_id (caching removed for security)");
            }
            catch (Exception ex)
            {
                logger?.LogError(ex,
                    "[VaultBootstrap] Failed to unwrap Vault:SecretId. The wrapper token may have expired (30min default TTL, set by WRAP_TTL_SECONDS in ci-stage-vault-creds.sh) or already been used. Re-run ci-stage-vault-creds.sh to issue a fresh wrapper.");
                throw;
            }
        }

        // Bootstrap runs before DI; if the caller didn't pass an authenticator,
        // construct one directly. Logger is intentionally null here because we
        // already have a bootstrap logger threaded through, and emitting two
        // log lines for one login event clutters startup output.
        authenticator ??= new VaultAppRoleAuthenticator();

        // Retry the AppRole login with exponential backoff. During deploys Vault
        // may be temporarily unavailable (immediate deploy strategy = brief
        // downtime). 5 attempts over ~62s (1+2+4+8+16s delays + call time)
        // covers the typical Fly machine restart window.
        const int maxAttempts = 5;
        VaultAppRoleLoginResult? login = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                login = await authenticator.LoginAsync(address, roleId, secretId, ct);
                break;
            }
            catch (Exception ex) when (attempt < maxAttempts && !ct.IsCancellationRequested
                && (ex is HttpRequestException
                    or TaskCanceledException
                    or TimeoutException
                    or VaultApiException { HttpStatusCode: >= System.Net.HttpStatusCode.InternalServerError }
                    or VaultApiException { HttpStatusCode: (System.Net.HttpStatusCode)429 }))
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
                logger?.LogWarning(ex,
                    "[VaultBootstrap] AppRole login attempt {Attempt}/{MaxAttempts} failed; retrying in {Delay}s",
                    attempt, maxAttempts, delay.TotalSeconds);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }

        var client = new VaultClient(new VaultClientSettings(address, new TokenAuthMethodInfo(login!.ClientToken)));

        var kvMountPoint = configuration["Vault:KvMountPoint"] ?? "secret";
        var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var mapping in kvMappings)
        {
            var (vaultPath, configPrefix, optional) = (mapping.VaultPath, mapping.ConfigPrefix, mapping.Optional);
            try
            {
                var kvStart = DateTime.UtcNow;
                var resp = await client.V1.Secrets.KeyValue.V2.ReadSecretAsync(
                    path: vaultPath, mountPoint: kvMountPoint);
                foreach (var (key, value) in resp.Data.Data)
                {
                    dict[$"{configPrefix}:{key}"] = value?.ToString();
                }
                VaultMetrics.KvReadDuration.Record((DateTime.UtcNow - kvStart).TotalSeconds, new KeyValuePair<string, object?>("path", vaultPath));
                logger?.LogInformation("[VaultBootstrap] Loaded {Count} keys from secret/{Path} -> {Prefix}",
                    resp.Data.Data.Count, vaultPath, configPrefix);
            }
            catch (Exception ex) when (optional)
            {
                // Optional path — skip cleanly if vault returns 404 / empty data.
                // Identity's OAuth providers are conditionally registered downstream
                // when blank, so a missing KV path is the same as "not configured".
                logger?.LogInformation(
                    "[VaultBootstrap] Optional secret/{Path} not present ({ExType}) — skipping",
                    vaultPath, ex.GetType().Name);
            }
            catch (Exception ex)
            {
                VaultMetrics.KvReadFailure.Add(1, new KeyValuePair<string, object?>("path", vaultPath), new KeyValuePair<string, object?>("error_type", ex.GetType().Name));
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

    /// <summary>
    /// Unwrap a response-wrapped secret_id. The wrapping_token is itself the
    /// auth — POST it to /v1/sys/wrapping/unwrap and Vault returns the
    /// originally-wrapped data. Single-use: a successful unwrap invalidates
    /// the token, and a second attempt fails.
    /// </summary>
    private static async Task<string> UnwrapSecretIdAsync(
        string vaultAddress, string wrappingToken, CancellationToken ct)
    {
        // Use a TokenAuthMethodInfo where the "token" IS the wrapping_token —
        // that's how Vault's wrapping API authenticates the unwrap call.
        var client = new VaultClient(new VaultClientSettings(
            vaultAddress, new TokenAuthMethodInfo(wrappingToken)));

        // Pass null to UnwrapWrappedResponseDataAsync — the wrapping token is
        // already on the auth method, and Vault uses it as both the auth and
        // the target of the unwrap when no token is passed in the body.
        Secret<Dictionary<string, object>> resp =
            await client.V1.System.UnwrapWrappedResponseDataAsync<Dictionary<string, object>>(
                tokenId: null);

        if (resp?.Data == null || !resp.Data.TryGetValue("secret_id", out var rawSecretId))
        {
            throw new InvalidOperationException(
                "Vault unwrap succeeded but response had no 'secret_id' key. " +
                "Was the wrapper token issued against an /auth/approle/.../secret-id endpoint?");
        }

        var secretId = rawSecretId?.ToString();
        if (string.IsNullOrWhiteSpace(secretId))
        {
            throw new InvalidOperationException(
                "Vault unwrap returned empty 'secret_id'.");
        }

        return secretId;
    }
}
