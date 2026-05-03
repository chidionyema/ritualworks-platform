#nullable enable
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Haworks.Identity.Application;
using Haworks.Identity.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Haworks.Identity.Infrastructure;

/// <summary>
/// JWT token generation and validation service.
/// </summary>
public class JwtTokenService : IJwtTokenService
{
    private readonly UserManager<User> _userManager;
    private readonly JwtOptions _jwtOptions;
    private readonly ILogger<JwtTokenService> _logger;
    private readonly ITokenRevocationService _revocationService;
    private readonly SymmetricSecurityKey _securityKey;
    private readonly IHostEnvironment _environment;

    public JwtTokenService(
        UserManager<User> userManager,
        IOptions<JwtOptions> jwtOptions,
        ILogger<JwtTokenService> logger,
        ITokenRevocationService revocationService,
        IHostEnvironment environment)
    {
        _userManager = userManager;
        _jwtOptions = jwtOptions.Value;
        _logger = logger;
        _revocationService = revocationService;
        _environment = environment;

        if (string.IsNullOrEmpty(_jwtOptions.Key))
        {
            _logger.LogCritical("JWT Key (Jwt:Key) is not configured");
            throw new InvalidOperationException("JWT Key is not configured.");
        }

        byte[] keyBytes;
        try
        {
            keyBytes = Convert.FromBase64String(_jwtOptions.Key);
        }
        catch (FormatException)
        {
            // Fallback to UTF-8 for plain-text keys in dev/test
            keyBytes = System.Text.Encoding.UTF8.GetBytes(_jwtOptions.Key);
        }

        if (keyBytes.Length < 32 && _environment.IsProduction())
        {
            _logger.LogCritical("JWT Key is too weak for production: {Length} bytes (minimum 32)", keyBytes.Length);
            throw new InvalidOperationException($"JWT Key is too weak for production. Expected at least 32 bytes, got {keyBytes.Length}.");
        }

        _securityKey = new SymmetricSecurityKey(keyBytes);
    }

    public async Task<JwtSecurityToken> GenerateTokenAsync(User user, DateTime expiration)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id),
            new(ClaimTypes.NameIdentifier, user.Id),
            new(ClaimTypes.Name, user.UserName!),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email!)
        };

        // Batch roles and claims query for better performance
        var roles = await _userManager.GetRolesAsync(user);
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));

        var userClaims = await _userManager.GetClaimsAsync(user);
        claims.AddRange(userClaims);

        var token = new JwtSecurityToken(
            issuer: _jwtOptions.Issuer,
            audience: _jwtOptions.Audience,
            claims: claims,
            expires: expiration,
            signingCredentials: new SigningCredentials(_securityKey, SecurityAlgorithms.HmacSha256)
        );

        _logger.LogInformation("Token generated for user {UserId} with {RoleCount} roles, expires {Expiry}",
            user.Id, roles.Count, expiration);

        return token;
    }

    public ClaimsPrincipal? ValidateToken(string tokenString, bool validateLifetime = true)
    {
        if (string.IsNullOrEmpty(tokenString))
        {
            return null;
        }

        var tokenHandler = new JwtSecurityTokenHandler();
        try
        {
            var validationParameters = GetTokenValidationParameters(validateLifetime);
            var principal = tokenHandler.ValidateToken(tokenString, validationParameters, out _);

            _logger.LogDebug("Token signature validated for {User}",
                principal?.Identity?.Name ?? "unknown");

            return principal;
        }
        catch (SecurityTokenExpiredException)
        {
            _logger.LogWarning("Token expired");
            return null;
        }
        catch (SecurityTokenInvalidSignatureException)
        {
            _logger.LogWarning("Invalid token signature");
            return null;
        }
        catch (SecurityTokenValidationException ex)
        {
            _logger.LogWarning(ex, "Token validation failed");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error validating token");
            return null;
        }
    }

    public async Task<ClaimsPrincipal?> ValidateTokenAsync(
        string tokenString,
        bool validateLifetime = true,
        CancellationToken ct = default)
    {
        // First validate signature and expiry
        var principal = ValidateToken(tokenString, validateLifetime);
        if (principal == null)
        {
            return null;
        }

        // Extract JTI for revocation check
        var jti = principal.FindFirstValue(JwtRegisteredClaimNames.Jti);
        if (string.IsNullOrEmpty(jti))
        {
            _logger.LogWarning("Token missing JTI claim, cannot check revocation");
            return principal; // Allow tokens without JTI (backwards compatibility)
        }

        // Check if token is revoked
        if (await _revocationService.IsTokenRevokedAsync(jti, ct))
        {
            _logger.LogWarning("Token {Jti} has been revoked", jti);
            return null;
        }

        _logger.LogInformation("Token fully validated for {User}",
            principal.Identity?.Name ?? "unknown");

        return principal;
    }

    public TokenValidationParameters GetTokenValidationParameters(bool validateLifetime = true)
    {
        return new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = _securityKey,
            ValidateIssuer = true,
            ValidIssuer = _jwtOptions.Issuer,
            ValidateAudience = true,
            ValidAudience = _jwtOptions.Audience,
            ValidateLifetime = validateLifetime,
            ClockSkew = TimeSpan.FromSeconds(AuthConstants.ClockSkewToleranceSeconds)
        };
    }

    public void SetSecureCookie(HttpContext context, JwtSecurityToken token)
    {
        var tokenString = new JwtSecurityTokenHandler().WriteToken(token);
        var cookieOptions = new CookieOptions
        {
            HttpOnly = true,
            Secure = _environment.IsProduction(),
            SameSite = SameSiteMode.Strict,
            Expires = token.ValidTo,
            Path = "/",
            IsEssential = true
        };
        context.Response.Cookies.Append("jwt", tokenString, cookieOptions);
    }

    public void DeleteAuthCookie(HttpContext context)
    {
        context.Response.Cookies.Delete("jwt", new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/"
        });
    }
}
