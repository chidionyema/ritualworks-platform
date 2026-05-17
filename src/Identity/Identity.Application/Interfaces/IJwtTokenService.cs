using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;

namespace Haworks.Identity.Application.Interfaces;

/// <summary>
/// Service for JWT token generation and validation.
/// </summary>
public interface IJwtTokenService
{
    /// <summary>
    /// Generates a JWT token for the specified user.
    /// </summary>
    Task<JwtSecurityToken> GenerateTokenAsync(User user, DateTime expiration, CancellationToken ct = default);

    /// <summary>
    /// Validates a JWT token string and returns the claims principal.
    /// Does NOT check token revocation - use ValidateTokenAsync for full validation.
    /// </summary>
    ClaimsPrincipal? ValidateToken(string tokenString, bool validateLifetime = true);

    /// <summary>
    /// Validates a JWT token string asynchronously, including revocation check.
    /// Returns null if the token is invalid, expired, or revoked.
    /// </summary>
    Task<ClaimsPrincipal?> ValidateTokenAsync(string tokenString, bool validateLifetime = true, CancellationToken ct = default);

    /// <summary>
    /// Gets token validation parameters.
    /// </summary>
    TokenValidationParameters GetTokenValidationParameters(bool validateLifetime = true);

    /// <summary>
    /// Sets a secure HTTP-only cookie containing the JWT.
    /// </summary>
    void SetSecureCookie(HttpContext context, JwtSecurityToken token);

    /// <summary>
    /// Deletes the auth cookie.
    /// </summary>
    void DeleteAuthCookie(HttpContext context);

    /// <summary>
    /// Generates a short-lived JWT for service-to-service calls.
    /// </summary>
    Task<string> GenerateServiceTokenAsync(DateTime expiration);
}
