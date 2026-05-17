namespace Haworks.Identity.Domain.Interfaces;

/// <summary>
/// Repository for RefreshToken operations.
/// </summary>
public interface IRefreshTokenRepository
{
    Task<RefreshToken?> GetByTokenAndUserIdAsync(string token, string userId, CancellationToken ct = default);
    Task AddAsync(RefreshToken refreshToken, CancellationToken ct = default);
    Task RemoveAsync(RefreshToken refreshToken, CancellationToken ct = default);
    Task RemoveAllForUserAsync(string userId, CancellationToken ct = default);
    Task<IDisposable> BeginTransactionAsync(CancellationToken ct = default);
    Task CommitTransactionAsync(CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
