using System.ComponentModel.DataAnnotations;

namespace Haworks.BuildingBlocks.Authentication;

/// <summary>
/// Configuration options for JWKS-based JWT validation.
/// Bound from the appsettings <c>Authentication:Jwks</c> section.
/// </summary>
/// <remarks>
/// Replaces the static-PEM model used by the legacy
/// <c>AddPlatformAuthentication</c> helper. Keys are fetched (and cached
/// + auto-refreshed) from <see cref="JwksUri"/> so that key rotation
/// performed by the Identity service does not require a redeploy of any
/// downstream API.
/// </remarks>
public sealed class JwksOptions
{
    public const string SectionName = "Authentication:Jwks";

    /// <summary>
    /// Absolute URL of the JWKS endpoint exposed by the Identity service
    /// (e.g. <c>https://identity.local/.well-known/jwks.json</c>).
    /// </summary>
    [Required]
    [Url]
    public string JwksUri { get; set; } = string.Empty;

    /// <summary>
    /// Expected <c>iss</c> claim value. Tokens with a different issuer
    /// are rejected.
    /// </summary>
    [Required]
    public string Issuer { get; set; } = string.Empty;

    /// <summary>
    /// Expected <c>aud</c> claim value. Each service typically sets this
    /// to its own scheme name (e.g. <c>haworks.orders</c>).
    /// </summary>
    [Required]
    public string Audience { get; set; } = string.Empty;

    /// <summary>
    /// How often the cached JWKS document is refreshed in the background.
    /// Defaults to 30 minutes — short enough for routine rotation, long
    /// enough to avoid hammering the identity service.
    /// </summary>
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// When <c>true</c> (the default) the
    /// <c>ConfigurationManager&lt;OpenIdConnectConfiguration&gt;</c>
    /// auto-refreshes keys at <see cref="RefreshInterval"/>. Set to
    /// <c>false</c> in tests that want to control refresh manually.
    /// </summary>
    public bool AutomaticRefresh { get; set; } = true;
}
