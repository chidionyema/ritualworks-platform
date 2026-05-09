using System.Security.Cryptography;
using Haworks.BuildingBlocks.Vault;
using Haworks.Identity.Infrastructure.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;

namespace Haworks.Identity.Api.Controllers;

/// <summary>
/// JWKS (JSON Web Key Set) endpoint per RFC 7517.
///
/// Downstream services fetch the public RSA key(s) from here to validate
/// JWTs signed by identity-svc — eliminating the need for a shared
/// symmetric secret across N services.
///
/// During key rotation the response contains both the new Active key AND
/// any recently-retired keys still inside their grace window, so tokens
/// signed under the previous key remain validatable until the grace window
/// elapses.
///
/// Standard discovery path: /.well-known/jwks.json
/// </summary>
[ApiController]
[AllowAnonymous]
[Route(".well-known/jwks.json")]
public sealed class JwksController : ControllerBase
{
    private readonly IJwtSigningKeyRing? _ring;
    private readonly IJwtSigningKeyProvider _signingKeyProvider;

    public JwksController(
        IJwtSigningKeyProvider signingKeyProvider,
        IJwtSigningKeyRing? ring = null)
    {
        _signingKeyProvider = signingKeyProvider;
        _ring = ring;
    }

    /// <summary>
    /// Returns the active public signing keys in JWK Set format.
    /// When the rotating ring is registered (Vault-backed deployments),
    /// this includes the Active key plus all Retiring keys still within
    /// their grace window. In Vault-disabled (config) deployments this
    /// returns the single configured key.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(JsonWebKeySetResponse), StatusCodes.Status200OK)]
    public ActionResult<JsonWebKeySetResponse> GetJwks()
    {
        var keys = _ring is not null
            ? _ring.AllValidKeys.Select(ToJwk).ToArray()
            : new[] { ToJwk(_signingKeyProvider.PublicJwk) };

        return Ok(new JsonWebKeySetResponse { Keys = keys });
    }

    private static JsonWebKeyResponse ToJwk(JwtSigningKeyEntry entry)
    {
        var rsa = entry.Key.Rsa
            ?? throw new InvalidOperationException(
                $"Signing key {entry.Kid} has no underlying RSA instance.");
        var pub = rsa.ExportParameters(includePrivateParameters: false);

        return new JsonWebKeyResponse
        {
            Kty = "RSA",
            Use = "sig",
            Alg = SecurityAlgorithms.RsaSha256,
            Kid = entry.Kid,
            N = Base64UrlEncoder.Encode(pub.Modulus!),
            E = Base64UrlEncoder.Encode(pub.Exponent!),
        };
    }

    private static JsonWebKeyResponse ToJwk(JsonWebKey jwk) => new()
    {
        Kty = jwk.Kty,
        Use = jwk.Use,
        Alg = jwk.Alg,
        Kid = jwk.Kid,
        N = jwk.N,
        E = jwk.E,
    };
}

/// <summary>JWK Set wire format — array of JWK keys.</summary>
public sealed class JsonWebKeySetResponse
{
    public IReadOnlyList<JsonWebKeyResponse> Keys { get; init; } = Array.Empty<JsonWebKeyResponse>();
}

/// <summary>RSA JWK wire format (per RFC 7518 §6.3).</summary>
public sealed class JsonWebKeyResponse
{
    public string Kty { get; init; } = "RSA";
    public string Use { get; init; } = "sig";
    public string Alg { get; init; } = SecurityAlgorithms.RsaSha256;
    public string? Kid { get; init; }
    public string? N { get; init; }   // modulus (base64url)
    public string? E { get; init; }   // exponent (base64url)
}
