#nullable enable
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Haworks.BuildingBlocks.Vault;
using Haworks.Identity.Application;
using Haworks.Identity.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Haworks.Identity.Infrastructure;

/// <summary>
/// JWT token generation and validation service.
///
/// Per ADR-0005: RS256 signing, RSA-2048 keypair sourced from
/// <see cref="IJwtSigningKeyProvider"/> (Vault-backed). Downstream services
/// validate via /.well-known/jwks.json — no shared secret to rotate across
/// services.
/// </summary>
public class JwtTokenService : IJwtTokenService
{
    private readonly UserManager<User> _userManager;
    private readonly JwtOptions _jwtOptions;
    private readonly ILogger<JwtTokenService> _logger;
    private readonly ITokenRevocationService _revocationService;
    private readonly IJwtSigningKeyProvider _signingKeyProvider;
    private readonly IHostEnvironment _environment;

    public JwtTokenService(
        UserManager<User> userManager,
        IOptions<JwtOptions> jwtOptions,
        ILogger<JwtTokenService> logger,
        ITokenRevocationService revocationService,
        IJwtSigningKeyProvider signingKeyProvider,
        IHostEnvironment environment)
    {
        _userManager = userManager;
        _jwtOptions = jwtOptions.Value;
        _logger = logger;
        _revocationService = revocationService;
        _signingKeyProvider = signingKeyProvider;
        _environment = environment;
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

        // RS256: sign with the private RSA key from Vault. The key's KeyId
        // is also written into the JWT header as `kid` so downstream
        // validators can pick the right public key from JWKS during rotation.
        var signingCredentials = new SigningCredentials(
            _signingKeyProvider.SigningKey, SecurityAlgorithms.RsaSha256);

        var token = new JwtSecurityToken(
            issuer: _jwtOptions.Issuer,
            audience: _jwtOptions.Audience,
            claims: claims,
            expires: expiration,
            signingCredentials: signingCredentials
        );
        token.Header["kid"] = _signingKeyProvider.KeyId;

        _logger.LogInformation("Token generated for user {UserId} with {RoleCount} roles, expires {Expiry}, kid {Kid}",
            user.Id, roles.Count, expiration, _signingKeyProvider.KeyId);

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

            // Extract JTI for revocation check
            var jti = principal?.FindFirst(JwtRegisteredClaimNames.Jti)?.Value;
            if (!string.IsNullOrEmpty(jti))
            {
                if (_revocationService.IsTokenRevoked(jti))
                {
                    _logger.LogWarning("Token {Jti} has been revoked (sync check)", jti);
                    return null;
                }
            }

            _logger.LogDebug("Token signature and revocation validated for {User}",
                principal?.Identity?.Name ?? "unknown");

            // Check revocation. IsTokenRevokedAsync is safe to block here: ASP.NET Core
            // runs on thread-pool threads (no SynchronizationContext), so GetAwaiter().GetResult()
            // cannot deadlock. Prefer ValidateTokenAsync for callers that are already async.
            var jti = principal?.FindFirstValue(JwtRegisteredClaimNames.Jti);
            if (!string.IsNullOrEmpty(jti) &&
                _revocationService.IsTokenRevokedAsync(jti).GetAwaiter().GetResult())
            {
                _logger.LogWarning("Token {Jti} has been revoked", jti);
                return null;
            }

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
        if (string.IsNullOrEmpty(tokenString))
        {
            return null;
        }

        var tokenHandler = new JwtSecurityTokenHandler();
        ClaimsPrincipal principal;
        try
        {
            var validationParameters = GetTokenValidationParameters(validateLifetime);
            principal = tokenHandler.ValidateToken(tokenString, validationParameters, out _);
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

        _logger.LogDebug("Token signature validated for {User}", principal.Identity?.Name ?? "unknown");

        // Extract JTI for revocation check
        var jti = principal.FindFirstValue(JwtRegisteredClaimNames.Jti);
        if (string.IsNullOrEmpty(jti))
        {
            _logger.LogWarning("Token missing JTI claim, cannot check revocation");
            return principal; // Allow tokens without JTI (backwards compatibility)
        }

        // Check if token is revoked using the proper async path
        if (await _revocationService.IsTokenRevokedAsync(jti, ct))
        {
            _logger.LogWarning("Token {Jti} has been revoked", jti);
            return null;
        }

        _logger.LogInformation("Token fully validated for {User}", principal.Identity?.Name ?? "unknown");

        return principal;
    }

    public TokenValidationParameters GetTokenValidationParameters(bool validateLifetime = true)
    {
        return new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = _signingKeyProvider.SigningKey,
            ValidAlgorithms = new[] { SecurityAlgorithms.RsaSha256 },
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
            Expires = new DateTimeOffset(token.ValidTo, TimeSpan.Zero),
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

    public Task<string> GenerateServiceTokenAsync(DateTime expiration)
    {
        var signingCredentials = new SigningCredentials(
            _signingKeyProvider.SigningKey, SecurityAlgorithms.RsaSha256);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, "bff-service"),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Role, "Service"),
        };

        var token = new JwtSecurityToken(
            issuer: _jwtOptions.Issuer,
            audience: _jwtOptions.Audience,
            claims: claims,
            expires: expiration,
            signingCredentials: signingCredentials);
        token.Header["kid"] = _signingKeyProvider.KeyId;

        return Task.FromResult(new JwtSecurityTokenHandler().WriteToken(token));
    }
}
