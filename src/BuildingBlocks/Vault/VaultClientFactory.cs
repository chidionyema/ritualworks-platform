using System.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Haworks.BuildingBlocks.Vault.Options;
using VaultSharp;
using VaultSharp.V1.AuthMethods;
using VaultSharp.V1.AuthMethods.Token;

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

        var secrets = await _fileReader.ReadSecretsAsync(
            options.RoleIdPath,
            options.SecretIdPath,
            options.HmacKeyPath,
            options.RequireHmacValidation,
            ct);

        if (!secrets.HmacValid && options.RequireHmacValidation)
            throw new SecurityException("HMAC validation failed and is required.");

        var login = await _authenticator.LoginAsync(options.Address, secrets.RoleId, secrets.SecretId, ct);

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
