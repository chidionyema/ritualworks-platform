using Microsoft.IdentityModel.Tokens;

namespace Haworks.Identity.Infrastructure.Authentication;

/// <summary>
/// One entry in the JWT signing key ring.
///
/// <para>
/// <see cref="Status"/> distinguishes the single Active key (used to sign
/// new tokens) from the zero-or-more Retiring keys we still publish in
/// JWKS so tokens signed under them remain validatable until
/// <see cref="RetiredAt"/> passes.
/// </para>
/// </summary>
public sealed record JwtSigningKeyEntry
{
    /// <summary>RSA security key carrying the private key (signing-capable).</summary>
    public required RsaSecurityKey Key { get; init; }

    /// <summary>
    /// Stable identifier — a SHA-256 digest of the public key parameters.
    /// The same PEM always produces the same Kid so consumers can cache
    /// JWK lookups by kid across restarts.
    /// </summary>
    public required string Kid { get; init; }

    /// <summary>Active = signs new tokens. Retiring = JWKS-only, no new signing.</summary>
    public required JwtSigningKeyStatus Status { get; init; }

    /// <summary>UTC timestamp the entry was added to the ring.</summary>
    public required DateTimeOffset AddedAt { get; init; }

    /// <summary>
    /// UTC timestamp at which a Retiring key should be dropped from the ring.
    /// Null while the key is Active.
    /// </summary>
    public DateTimeOffset? RetiredAt { get; init; }
}

/// <summary>Lifecycle states for a key in the signing ring.</summary>
public enum JwtSigningKeyStatus
{
    /// <summary>Currently used to sign new tokens.</summary>
    Active,

    /// <summary>
    /// No longer signing new tokens; still accepted by validators and
    /// published in JWKS until the grace window expires.
    /// </summary>
    Retiring,
}
