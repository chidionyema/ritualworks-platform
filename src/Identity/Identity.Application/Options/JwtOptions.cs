using System.ComponentModel.DataAnnotations;

namespace Haworks.Identity.Application.Options;

/// <summary>
/// Configuration options for JWT token generation and validation.
/// </summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Key { get; set; } = string.Empty;

    public string Issuer { get; set; } = string.Empty;

    public string Audience { get; set; } = string.Empty;

    /// <summary>
    /// Access token expiry in minutes. Default 15, range 5-60.
    /// Shorter tokens reduce risk of token theft.
    /// </summary>
    [Range(5, 60, ErrorMessage = "TokenExpiryMinutes must be between 5 and 60")]
    public int TokenExpiryMinutes { get; set; } = 15;

    /// <summary>
    /// Refresh token expiry in days. Default 7, range 1-90.
    /// </summary>
    [Range(1, 90, ErrorMessage = "RefreshTokenExpiryDays must be between 1 and 90")]
    public int RefreshTokenExpiryDays { get; set; } = 7;

    /// <summary>
    /// Overlap window in minutes during which both old and new JWT signing keys
    /// are valid after a key rotation. Default 15.
    /// </summary>
    [Range(5, 120, ErrorMessage = "OverlapMinutes must be between 5 and 120")]
    public int OverlapMinutes { get; set; } = 15;
}
