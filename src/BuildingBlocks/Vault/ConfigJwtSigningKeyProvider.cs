using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Haworks.BuildingBlocks.Vault;

/// <summary>
/// IJwtSigningKeyProvider that loads the RSA private key from configuration
/// instead of Vault. Used when Vault is disabled (e.g. on Fly.io where there
/// is no Vault container). Reads <c>Jwt:SigningKeyPem</c> — accepts either a
/// raw PEM string or its base64-encoded form (env vars are usually one-line).
/// </summary>
public sealed class ConfigJwtSigningKeyProvider : IJwtSigningKeyProvider
{
    public string KeyId { get; }
    public RsaSecurityKey SigningKey { get; }
    public JsonWebKey PublicJwk { get; }

    public ConfigJwtSigningKeyProvider(string privateKeyPem, string keyId)
    {
        var pem = privateKeyPem.Contains("-----BEGIN", StringComparison.Ordinal)
            ? privateKeyPem
            : Encoding.UTF8.GetString(Convert.FromBase64String(privateKeyPem));

        var rsa = RSA.Create();
        rsa.ImportFromPem(pem);

        SigningKey = new RsaSecurityKey(rsa) { KeyId = keyId };
        KeyId = keyId;

        var pub = rsa.ExportParameters(includePrivateParameters: false);
        PublicJwk = new JsonWebKey
        {
            Kty = "RSA",
            Use = "sig",
            Alg = SecurityAlgorithms.RsaSha256,
            Kid = keyId,
            N = Base64UrlEncoder.Encode(pub.Modulus!),
            E = Base64UrlEncoder.Encode(pub.Exponent!),
        };
    }
}
