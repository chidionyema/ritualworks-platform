namespace Haworks.Identity.Application.Constants;

/// <summary>
/// Centralized constants for authentication and authorization operations.
/// </summary>
public static class AuthConstants
{
    /// <summary>
    /// Default JWT token expiration time in minutes.
    /// </summary>
    public const int DefaultTokenExpiryMinutes = 15;

    /// <summary>
    /// Default refresh token expiration time in days.
    /// </summary>
    public const int DefaultRefreshTokenExpiryDays = 7;

    /// <summary>
    /// Clock skew tolerance for token validation in seconds.
    /// Keep minimal for security - 5 seconds is sufficient for minor clock drift.
    /// </summary>
    public const int ClockSkewToleranceSeconds = 5;

    /// <summary>
    /// Maximum failed login attempts before lockout.
    /// </summary>
    public const int MaxFailedLoginAttempts = 5;

    /// <summary>
    /// Lockout duration in minutes after max failed attempts.
    /// </summary>
    public const int LockoutDurationMinutes = 15;
}

/// <summary>
/// Configuration keys for authentication settings.
/// Use JwtOptions.SectionName for the section name.
/// </summary>
public static class AuthConfigKeys
{
    public const string JwtKey = "Jwt:Key";
    public const string JwtIssuer = "Jwt:Issuer";
    public const string JwtAudience = "Jwt:Audience";
    public const string JwtExpiryMinutes = "Jwt:TokenExpiryMinutes";
    public const string JwtRefreshTokenExpiryDays = "Jwt:RefreshTokenExpiryDays";
}
