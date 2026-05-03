using Microsoft.IdentityModel.Tokens;

namespace Haworks.BuildingBlocks.Vault;

/// <summary>
/// Provides the RSA signing key used to sign JWTs (private key) plus the
/// matching public key for JWKS publication. Implementations are responsible
/// for generating + persisting the keypair on first run, and loading it
/// on subsequent restarts so tokens issued before a restart remain valid.
///
/// Per ADR-0005: identity-svc signs JWTs with RS256; downstream services
/// validate via /.well-known/jwks.json without needing a shared secret.
/// </summary>
public interface IJwtSigningKeyProvider
{
    /// <summary>
    /// Stable key identifier — included in the JWT header as <c>kid</c> so
    /// validators can pick the right public key from JWKS when key rotation
    /// is in progress.
    /// </summary>
    string KeyId { get; }

    /// <summary>The RSA security key used for signing (carries private key).</summary>
    RsaSecurityKey SigningKey { get; }

    /// <summary>
    /// The matching public key in JWK format for /.well-known/jwks.json.
    /// </summary>
    JsonWebKey PublicJwk { get; }
}
