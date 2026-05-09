using Haworks.BuildingBlocks.Vault;
using Microsoft.IdentityModel.Tokens;

namespace Haworks.Identity.Infrastructure.Authentication;

/// <summary>
/// <see cref="IJwtSigningKeyProvider"/> adapter over <see cref="IJwtSigningKeyRing"/>.
///
/// <para>
/// JwtTokenService and JwtBearerOptions late-binding both consume
/// <see cref="IJwtSigningKeyProvider"/>. Rather than rewrite those callers
/// to know about the ring, this adapter keeps the existing single-key
/// surface area: <see cref="SigningKey"/> and <see cref="KeyId"/> always
/// reflect the ring's current Active entry, while <see cref="PublicJwk"/>
/// returns the JWK for that same Active key.
/// </para>
///
/// <para>
/// Validators that need to accept tokens signed under a recently-retired
/// key go through JWKS (see <c>JwksController</c>), not through this
/// provider — by design, the provider exposes the issuing key only.
/// </para>
/// </summary>
public sealed class RotatingJwtSigningKeyProvider : IJwtSigningKeyProvider
{
    private readonly IJwtSigningKeyRing _ring;

    public RotatingJwtSigningKeyProvider(IJwtSigningKeyRing ring)
    {
        _ring = ring;
    }

    public string KeyId => _ring.Active.Kid;

    public RsaSecurityKey SigningKey => _ring.Active.Key;

    public JsonWebKey PublicJwk
    {
        get
        {
            var active = _ring.Active;
            var rsa = active.Key.Rsa
                ?? throw new InvalidOperationException(
                    "Active signing key has no underlying RSA instance.");
            var pub = rsa.ExportParameters(includePrivateParameters: false);

            return new JsonWebKey
            {
                Kty = "RSA",
                Use = "sig",
                Alg = SecurityAlgorithms.RsaSha256,
                Kid = active.Kid,
                N = Base64UrlEncoder.Encode(pub.Modulus!),
                E = Base64UrlEncoder.Encode(pub.Exponent!),
            };
        }
    }
}
