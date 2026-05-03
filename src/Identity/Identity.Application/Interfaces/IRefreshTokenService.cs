
namespace Haworks.Identity.Application.Interfaces;

/// <summary>
/// Service for refresh token management.
/// </summary>
public interface IRefreshTokenService
{
    /// <summary>
    /// Generates a new refresh token for the user.
    /// </summary>
    Task<RefreshToken> GenerateRefreshTokenAsync(string userId, CancellationToken ct = default);

    /// <summary>
    /// Revokes all refresh tokens for a user.
    /// </summary>
    Task RevokeRefreshTokensForUserAsync(string userId, CancellationToken ct = default);
}
