namespace Haworks.Identity.Application.Interfaces;

/// <summary>
/// Service for token revocation management.
/// </summary>
public interface ITokenRevocationService
{
    /// <summary>
    /// Revokes a token.
    /// </summary>
    Task RevokeTokenAsync(string tokenValue, string userId, DateTime expiryDate, CancellationToken ct = default);

    /// <summary>
    /// Checks if a token has been revoked.
    /// </summary>
    Task<bool> IsTokenRevokedAsync(string tokenValue, CancellationToken ct = default);
}
