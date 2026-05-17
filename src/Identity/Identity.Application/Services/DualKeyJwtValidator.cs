using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Haworks.Identity.Application.Options;

namespace Haworks.Identity.Application.Services;

/// <summary>
/// Validates JWT tokens using both the current and previous signing keys
/// during the overlap window after a key rotation. The previous key is
/// cleared after the configured overlap period.
/// </summary>
public sealed class DualKeyJwtValidator
{
    private readonly IOptionsMonitor<JwtOptions> _jwtOptions;
    private readonly ILogger<DualKeyJwtValidator> _logger;

    private SecurityKey? _previousKey;
    private DateTimeOffset _previousKeyExpiresAt = DateTimeOffset.MinValue;
    private readonly object _lock = new();

    public DualKeyJwtValidator(
        IOptionsMonitor<JwtOptions> jwtOptions,
        ILogger<DualKeyJwtValidator> logger)
    {
        _jwtOptions = jwtOptions;
        _logger = logger;
    }

    /// <summary>
    /// Sets the previous key for the overlap window.
    /// </summary>
    /// <param name="previousKey">The previous signing key.</param>
    /// <param name="overlapMinutes">How long to keep the previous key valid. Default 15.</param>
    public void SetPreviousKey(SecurityKey previousKey, int? overlapMinutes = null)
    {
        var overlap = overlapMinutes ?? _jwtOptions.CurrentValue.OverlapMinutes;
        lock (_lock)
        {
            _previousKey = previousKey;
            _previousKeyExpiresAt = DateTimeOffset.UtcNow.AddMinutes(overlap);
        }

        _logger.LogInformation(
            "Previous JWT key set for overlap; expires at {ExpiresAt:O}",
            _previousKeyExpiresAt);
    }

    /// <summary>
    /// Clears the previous key (called after overlap window elapses).
    /// </summary>
    public void ClearPreviousKey()
    {
        lock (_lock)
        {
            _previousKey = null;
            _previousKeyExpiresAt = DateTimeOffset.MinValue;
        }

        _logger.LogInformation("Previous JWT key cleared after overlap window");
    }

    /// <summary>
    /// Gets the previous key if still within the overlap window, null otherwise.
    /// </summary>
    public SecurityKey? GetPreviousKeyIfValid()
    {
        lock (_lock)
        {
            if (_previousKey is not null && DateTimeOffset.UtcNow < _previousKeyExpiresAt)
            {
                return _previousKey;
            }

            if (_previousKey is not null)
            {
                // Overlap window expired — clear it
                _previousKey = null;
                _previousKeyExpiresAt = DateTimeOffset.MinValue;
                _logger.LogInformation("Previous JWT key expired (overlap window elapsed)");
            }

            return null;
        }
    }

    /// <summary>
    /// Validates a token trying the current key first, then the previous key
    /// if within the overlap window.
    /// </summary>
    public ClaimsPrincipal? ValidateToken(
        string token,
        SecurityKey currentKey,
        TokenValidationParameters baseParameters)
    {
        var handler = new JwtSecurityTokenHandler();

        // Try current key first
        try
        {
            var parameters = baseParameters.Clone();
            parameters.IssuerSigningKey = currentKey;
            return handler.ValidateToken(token, parameters, out _);
        }
        catch (SecurityTokenSignatureKeyNotFoundException)
        {
            // Fall through to try previous key
        }
        catch (SecurityTokenInvalidSignatureException)
        {
            // Fall through to try previous key
        }

        // Try previous key if available
        var previousKey = GetPreviousKeyIfValid();
        if (previousKey is null)
        {
            _logger.LogDebug("Token failed validation with current key and no previous key available");
            return null;
        }

        try
        {
            var parameters = baseParameters.Clone();
            parameters.IssuerSigningKey = previousKey;
            var principal = handler.ValidateToken(token, parameters, out _);
            _logger.LogDebug("Token validated with previous key (overlap window active)");
            return principal;
        }
        catch (SecurityTokenException ex)
        {
            _logger.LogDebug(ex, "Token failed validation with both current and previous keys");
            return null;
        }
    }
}
