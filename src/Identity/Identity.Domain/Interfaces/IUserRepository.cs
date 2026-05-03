
namespace Haworks.Identity.Domain.Interfaces;

/// <summary>
/// Repository for User operations.
/// </summary>
public interface IUserRepository
{
    Task<User?> GetUserByIdAsync(string userId, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
