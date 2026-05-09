using Microsoft.IdentityModel.Tokens;

namespace Haworks.Identity.Infrastructure.Authentication;

/// <summary>
/// A ring of RSA signing keys used by identity-svc to sign JWTs.
///
/// Supports zero-downtime rotation: at any moment there is exactly one
/// <see cref="Active"/> key (used to sign new tokens), plus zero or more
/// recently-retired-but-still-valid keys kept around so tokens issued under
/// them are still accepted by validators (i.e. published in JWKS) until
/// their grace window expires.
///
/// Implementations MUST be thread-safe — the ring is a singleton and is
/// read by every JWT signing/validation path concurrently while the
/// background rotation worker mutates it.
/// </summary>
public interface IJwtSigningKeyRing
{
    /// <summary>
    /// The current "issuing" key. New tokens are signed with this key and
    /// carry its <see cref="JwtSigningKeyEntry.Kid"/> in the JWT header.
    /// Throws if the ring has not been initialized yet.
    /// </summary>
    JwtSigningKeyEntry Active { get; }

    /// <summary>
    /// Immutable snapshot of every key validators should still accept —
    /// the active key plus any retiring keys still within their grace
    /// window. Used to populate /.well-known/jwks.json.
    /// </summary>
    IReadOnlyList<JwtSigningKeyEntry> AllValidKeys { get; }
}
