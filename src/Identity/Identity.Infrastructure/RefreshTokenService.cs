#nullable enable
using System.Security.Cryptography;
using Haworks.Identity.Application;
using Haworks.Identity.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Haworks.Identity.Infrastructure;

/// <summary>
/// Service for managing refresh tokens.
/// </summary>
public class RefreshTokenService : IRefreshTokenService
{
    private readonly AppIdentityDbContext _context;
    private readonly JwtOptions _jwtOptions;
    private readonly ILogger<RefreshTokenService> _logger;

    public RefreshTokenService(
        AppIdentityDbContext context,
        IOptions<JwtOptions> jwtOptions,
        ILogger<RefreshTokenService> logger)
    {
        _context = context;
        _jwtOptions = jwtOptions.Value;
        _logger = logger;
    }

    public async Task<RefreshToken> GenerateRefreshTokenAsync(string userId, CancellationToken ct = default)
    {
        var newTokenValue = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        var expires = DateTime.UtcNow.AddDays(_jwtOptions.RefreshTokenExpiryDays);
        var refreshToken = RefreshToken.Create(userId, newTokenValue, expires);

        _context.RefreshTokens.Add(refreshToken);
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation("Refresh token generated for user {UserId}, expires {Expiry}",
            userId, refreshToken.Expires);

        return refreshToken;
    }

    public async Task RevokeRefreshTokensForUserAsync(string userId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(userId))
        {
            _logger.LogWarning("RevokeRefreshTokensForUserAsync called with null userId");
            return;
        }

        var deletedCount = await _context.RefreshTokens
            .Where(rt => rt.UserId == userId)
            .ExecuteDeleteAsync(ct);

        if (deletedCount != 0)
        {
            _logger.LogInformation("Revoked {Count} refresh tokens for user {UserId}",
                deletedCount, userId);
        }
    }
}
