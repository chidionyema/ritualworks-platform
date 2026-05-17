using Microsoft.EntityFrameworkCore;
using Haworks.Identity.Domain;
using Haworks.Identity.Domain;

namespace Haworks.Identity.Infrastructure;

/// <summary>
/// Repository for User operations using AppIdentityDbContext.
/// </summary>
public class IdentityUserRepository : IUserRepository
{
    private readonly AppIdentityDbContext _context;

    public IdentityUserRepository(
        AppIdentityDbContext context,
        ILogger<IdentityUserRepository> logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public async Task<User?> GetUserByIdAsync(string userId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(userId)) return null;

        return await _context.Users
            .AsNoTracking()
            .Include(u => u.Profile)
            .FirstOrDefaultAsync(u => u.Id == userId, ct);
    }

    public Task SaveChangesAsync(CancellationToken ct = default)
    {
        return _context.SaveChangesAsync(ct);
    }
}

/// <summary>
/// Repository for UserProfile operations using AppIdentityDbContext.
/// </summary>
public class IdentityUserProfileRepository : IUserProfileRepository
{
    private readonly AppIdentityDbContext _context;

    public IdentityUserProfileRepository(
        AppIdentityDbContext context,
        ILogger<IdentityUserProfileRepository> logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public async Task<UserProfile?> GetByUserIdAsync(string userId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(userId)) return null;

        return await _context.UserProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId, ct);
    }

    public async Task AddAsync(UserProfile profile, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        await _context.UserProfiles.AddAsync(profile, ct);
        await _context.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(UserProfile profile, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var existing = await _context.UserProfiles.FindAsync(new object[] { profile.Id }, ct);
        if (existing != null)
        {
            _context.Entry(existing).CurrentValues.SetValues(profile);
        }
        else
        {
            _context.UserProfiles.Update(profile);
        }

        await _context.SaveChangesAsync(ct);
    }

    public Task SaveChangesAsync(CancellationToken ct = default)
    {
        return _context.SaveChangesAsync(ct);
    }
}

/// <summary>
/// Repository for RefreshToken operations using AppIdentityDbContext.
/// </summary>
public class IdentityRefreshTokenRepository : IRefreshTokenRepository
{
    private readonly AppIdentityDbContext _context;

    public IdentityRefreshTokenRepository(
        AppIdentityDbContext context,
        ILogger<IdentityRefreshTokenRepository> logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public async Task<RefreshToken?> GetByTokenAndUserIdAsync(string token, string userId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(userId)) return null;

        // Don't Include(rt => rt.User): the refresh-token flow already loads
        // the user via UserManager (which tracks it). Eager-loading User here
        // produces a second detached User instance with the same key, and the
        // subsequent RemoveAsync's Attach pass tries to track both — EF
        // throws "another instance with the same key value is already being
        // tracked". Callers that need the user query it themselves.
        return await _context.RefreshTokens
            .AsNoTracking()
            .FirstOrDefaultAsync(rt => rt.Token == token && rt.UserId == userId, ct);
    }

    public async Task AddAsync(RefreshToken refreshToken, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(refreshToken);

        await _context.RefreshTokens.AddAsync(refreshToken, ct);
        await _context.SaveChangesAsync(ct);
    }

    public Task RemoveAsync(RefreshToken refreshToken, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(refreshToken);

        _context.RefreshTokens.Remove(refreshToken);
        return _context.SaveChangesAsync(ct);
    }

    public async Task RemoveAllForUserAsync(string userId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(userId)) return;

        await _context.RefreshTokens
            .Where(rt => rt.UserId == userId)
            .ExecuteDeleteAsync(ct);
    }

    public async Task<IDisposable> BeginTransactionAsync(CancellationToken ct = default)
    {
        return await _context.Database.BeginTransactionAsync(ct);
    }

    public async Task CommitTransactionAsync(CancellationToken ct = default)
    {
        var transaction = _context.Database.CurrentTransaction
            ?? throw new InvalidOperationException("No active transaction to commit.");
        await transaction.CommitAsync(ct);
    }

    public Task SaveChangesAsync(CancellationToken ct = default)
    {
        return _context.SaveChangesAsync(ct);
    }
}
