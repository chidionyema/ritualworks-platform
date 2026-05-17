using System.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Haworks.BuildingBlocks.Vault.Options;
using VaultSharp;
using VaultSharp.V1.AuthMethods;
using VaultSharp.V1.AuthMethods.Token;
using VaultSharp.V1.Commons;

namespace Haworks.BuildingBlocks.Vault;

/// <summary>
/// Builds the runtime <see cref="IVaultClient"/> used by <see cref="VaultService"/>.
///
/// AppRole login is delegated to <see cref="IVaultAppRoleAuthenticator"/> — the
/// same component the startup config bootstrap uses — and the resulting token
/// is wrapped in a <see cref="TokenAuthMethodInfo"/>. We deliberately avoid
/// VaultSharp's <c>AppRoleAuthMethodInfo</c> login provider because it
/// rewrites token-bearing requests in a way Vault rejects with permission
/// denied for KV reads (see <see cref="VaultAppRoleAuthenticator"/>).
/// </summary>
public class VaultClientFactory : IVaultClientFactory
{
    private readonly ISecretFileReader _fileReader;
    private readonly ICertificateValidator _certValidator;
    private readonly IVaultAppRoleAuthenticator _authenticator;
    private readonly ILogger<VaultClientFactory> _logger;

    // Wrapping tokens are single-use. Cache the unwrapped secret_id so
    // re-auths (when the AppRole token lease expires) don't try to unwrap
    // an already-consumed wrapper. Singleton lifetime makes per-instance
    // caching safe for the lifetime of the process.
    private string? _cachedUnwrappedSecretId;
    private readonly SemaphoreSlim _unwrapGate = new(1, 1);

    public VaultClientFactory(
        ISecretFileReader fileReader,
        ICertificateValidator certValidator,
        IVaultAppRoleAuthenticator authenticator,
        ILogger<VaultClientFactory> logger)
    {
        _fileReader = fileReader;
        _certValidator = certValidator;
        _authenticator = authenticator;
        _logger = logger;
    }

    public async Task<VaultClientHandle> CreateClientAsync(VaultOptions options, CancellationToken ct)
    {
        _logger.LogInformation("[VaultClientFactory] Authenticating to {Address} via AppRole", options.Address);

        string roleId;
        string secretId;
        bool hmacValid = true;

        // Direct creds (Vault:RoleId + Vault:SecretId) take precedence over
        // legacy file-path creds. Mirrors VaultConfigBootstrap.LoadAsync so
        // bootstrap-time and runtime auth share one truth — services that
        // never call VaultConfigBootstrap (e.g. orders' DynamicCredentials
        // ConnectionInterceptor) still need to authenticate at runtime.
        if (!string.IsNullOrWhiteSpace(options.RoleId) && !string.IsNullOrWhiteSpace(options.SecretId))
        {
            roleId = options.RoleId.Trim();
            secretId = await ResolveSecretIdAsync(options, ct);
        }
        else
        {
            var secrets = await _fileReader.ReadSecretsAsync(
                options.RoleIdPath,
                options.SecretIdPath,
                options.HmacKeyPath,
                options.RequireHmacValidation,
                ct);
            roleId = secrets.RoleId;
            secretId = secrets.SecretId;
            hmacValid = secrets.HmacValid;
        }

        if (!hmacValid && options.RequireHmacValidation)
            throw new SecurityException("HMAC validation failed and is required.");

        var login = await _authenticator.LoginAsync(options.Address, roleId, secretId, ct);

        var handler = CreateHttpClientHandler(options);
        var httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(options.HttpTimeoutSeconds)
        };

        IAuthMethodInfo authMethod = new TokenAuthMethodInfo(login.ClientToken);
        var settings = new VaultClientSettings(options.Address, authMethod)
        {
            MyHttpClientProviderFunc = _ => httpClient
        };

        var client = new VaultSharp.VaultClient(settings);
        return new VaultClientHandle(client, DateTime.UtcNow, login.LeaseDuration);
    }

    /// <summary>
    /// Returns the raw secret_id for AppRole login. If the configured SecretId
    /// is a Vault response-wrapping token (SecretIdIsWrapped=true), unwraps it
    /// once and caches the result for subsequent re-auths — wrapping tokens
    /// are single-use, so a second unwrap attempt would fail.
    /// </summary>
    private async Task<string> ResolveSecretIdAsync(VaultOptions options, CancellationToken ct)
    {
        if (!options.SecretIdIsWrapped)
            return options.SecretId.Trim();

        if (_cachedUnwrappedSecretId is not null)
            return _cachedUnwrappedSecretId;

        if (!await _unwrapGate.WaitAsync(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false))
            throw new TimeoutException("Vault unwrap gate timed out after 60s");
        try
        {
            if (_cachedUnwrappedSecretId is not null)
                return _cachedUnwrappedSecretId;

            _logger.LogInformation(
                "[VaultClientFactory] Unwrapping wrapped AppRole SecretId (one-time per process)");

            // The wrapping_token IS the auth — POST it to /v1/sys/wrapping/unwrap
            // and Vault returns the originally-wrapped data.
            var unwrapClient = new VaultSharp.VaultClient(new VaultClientSettings(
                options.Address, new TokenAuthMethodInfo(options.SecretId.Trim())));

            Secret<Dictionary<string, object>> resp =
                await unwrapClient.V1.System
                    .UnwrapWrappedResponseDataAsync<Dictionary<string, object>>(tokenId: null);

            if (resp?.Data == null || !resp.Data.TryGetValue("secret_id", out var rawSecretId))
                throw new InvalidOperationException(
                    "Vault unwrap succeeded but response had no 'secret_id' key. " +
                    "Was the wrapper token issued against /auth/approle/.../secret-id?");

            var unwrapped = rawSecretId?.ToString();
            if (string.IsNullOrWhiteSpace(unwrapped))
                throw new InvalidOperationException("Vault unwrap returned empty 'secret_id'.");

            _cachedUnwrappedSecretId = unwrapped;
            return _cachedUnwrappedSecretId;
        }
        finally
        {
            _unwrapGate.Release();
        }
    }

    private HttpClientHandler CreateHttpClientHandler(VaultOptions options)
    {
        var handler = new HttpClientHandler
        {
            SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            CheckCertificateRevocationList = options.EnableCrlChecks,
            ServerCertificateCustomValidationCallback = (_, cert, chain, errors) =>
                _certValidator.ValidateServerCertificate(cert as X509Certificate2, chain, errors, options)
        };
        return handler;
    }
}
