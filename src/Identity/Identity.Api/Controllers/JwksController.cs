using Haworks.BuildingBlocks.Vault;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;

namespace Haworks.Identity.Api.Controllers;

/// <summary>
/// JWKS (JSON Web Key Set) endpoint per RFC 7517.
/// Downstream services fetch the public RSA key from here to validate JWTs
/// signed by identity-svc — eliminating the need for a shared symmetric
/// secret to be distributed and rotated across N services.
///
/// Standard discovery path: /.well-known/jwks.json
/// </summary>
[ApiController]
[Route(".well-known")]
public sealed class JwksController : ControllerBase
{
    private readonly IJwtSigningKeyProvider _signingKeyProvider;

    public JwksController(IJwtSigningKeyProvider signingKeyProvider)
    {
        _signingKeyProvider = signingKeyProvider;
    }

    /// <summary>
    /// Returns the active public signing keys in JWK Set format.
    /// During key rotation this would return both the previous and new
    /// keys so consumers can validate tokens signed by either; for now
    /// it's a single-key set.
    /// </summary>
    [HttpGet("jwks.json")]
    [ProducesResponseType(typeof(JsonWebKeySetResponse), StatusCodes.Status200OK)]
    public ActionResult<JsonWebKeySetResponse> GetJwks()
    {
        var publicJwk = _signingKeyProvider.PublicJwk;
        return Ok(new JsonWebKeySetResponse
        {
            Keys = new[]
            {
                new JsonWebKeyResponse
                {
                    Kty = publicJwk.Kty,
                    Use = publicJwk.Use,
                    Alg = publicJwk.Alg,
                    Kid = publicJwk.Kid,
                    N = publicJwk.N,
                    E = publicJwk.E,
                }
            }
        });
    }
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
