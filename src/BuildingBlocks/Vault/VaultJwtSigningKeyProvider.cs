using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;
using VaultSharp;
using VaultSharp.V1.AuthMethods.Token;

namespace Haworks.BuildingBlocks.Vault;

/// <summary>
/// Reads the RSA signing keypair from Vault KV at <c>secret/{service}/jwt-signing</c>.
/// On first run (key absent), generates a fresh RSA-2048 keypair and writes
/// it back so subsequent restarts find it. Vault dev mode is in-memory, so
/// the key is regenerated each time the Vault container restarts — that's
/// fine for dev (existing tokens become invalid, force re-login).
///
/// Thread-safety: <see cref="InitializeAsync"/> must be called once at
/// startup before the service handles requests. Properties throw if
/// accessed before initialization.
/// </summary>
public sealed class VaultJwtSigningKeyProvider : IJwtSigningKeyProvider
{
    private readonly string _vaultAddress;
    private readonly string _serviceName;
    private readonly IVaultAppRoleAuthenticator _authenticator;
    private readonly string _roleId;
    private readonly string _secretId;

    private RsaSecurityKey? _signingKey;
    private JsonWebKey? _publicJwk;
    private string? _keyId;

    public VaultJwtSigningKeyProvider(
        string vaultAddress,
        string serviceName,
        IVaultAppRoleAuthenticator authenticator,
        string roleId,
        string secretId)
    {
        _vaultAddress = vaultAddress;
        _serviceName  = serviceName;
        _authenticator = authenticator;
        _roleId = roleId;
        _secretId = secretId;
    }

    public string KeyId => _keyId
        ?? throw new InvalidOperationException("VaultJwtSigningKeyProvider not initialized.");

    public RsaSecurityKey SigningKey => _signingKey
        ?? throw new InvalidOperationException("VaultJwtSigningKeyProvider not initialized.");

    public JsonWebKey PublicJwk => _publicJwk
        ?? throw new InvalidOperationException("VaultJwtSigningKeyProvider not initialized.");

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var login = await _authenticator.LoginAsync(_vaultAddress, _roleId, _secretId, ct);
        var client = new VaultClient(new VaultClientSettings(_vaultAddress, new TokenAuthMethodInfo(login.ClientToken)));

        var path = $"{_serviceName}/jwt-signing";

        // Try to read an existing keypair first.
        string? privatePem = null;
        string? kid = null;
        try
        {
            var resp = await client.V1.Secrets.KeyValue.V2.ReadSecretAsync(path: path, mountPoint: "secret");
            if (resp.Data.Data.TryGetValue("PrivateKeyPem", out var privObj))
            {
                privatePem = privObj?.ToString();
            }
            if (resp.Data.Data.TryGetValue("KeyId", out var kidObj))
            {
                kid = kidObj?.ToString();
            }
        }
        catch (VaultSharp.Core.VaultApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // First run — keypair doesn't exist yet, generate below.
        }

        if (string.IsNullOrEmpty(privatePem))
        {
            // Generate fresh keypair.
            using var fresh = RSA.Create(2048);
            privatePem = fresh.ExportRSAPrivateKeyPem();
            kid = Guid.NewGuid().ToString("N");

            // Persist back to Vault so restarts find it.
            await client.V1.Secrets.KeyValue.V2.WriteSecretAsync(
                path: path,
                data: new Dictionary<string, object>
                {
                    ["PrivateKeyPem"] = privatePem,
                    ["KeyId"] = kid,
                    ["GeneratedAt"] = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                },
                mountPoint: "secret");
        }

        // Materialize the RSA key. Note: the RSA instance owned by the
        // RsaSecurityKey must outlive the provider, so we don't dispose it
        // here (provider lifetime == process lifetime).
        var rsa = RSA.Create();
        rsa.ImportFromPem(privatePem);

        _signingKey = new RsaSecurityKey(rsa) { KeyId = kid };
        _keyId = kid!;

        // Derive public-only JWK for /.well-known/jwks.json publication.
        var publicParams = rsa.ExportParameters(includePrivateParameters: false);
        _publicJwk = new JsonWebKey
        {
            Kty = "RSA",
            Use = "sig",
            Alg = SecurityAlgorithms.RsaSha256,
            Kid = kid,
            N = Base64UrlEncoder.Encode(publicParams.Modulus!),
            E = Base64UrlEncoder.Encode(publicParams.Exponent!),
        };
    }
}
