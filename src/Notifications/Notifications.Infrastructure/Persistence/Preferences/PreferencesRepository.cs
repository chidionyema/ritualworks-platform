using Haworks.Notifications.Application.Preferences;
using Haworks.Notifications.Domain.Entities;
using Haworks.Notifications.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Haworks.Notifications.Infrastructure.Persistence.PreferencesStore;

/// <summary>
/// EF Core repository for the <c>NotificationPreferences</c> and
/// <c>RateLimitBuckets</c> tables. Reads use AsNoTracking; writes go through
/// the change tracker so EF can hydrate private setters via reflection.
/// </summary>
public sealed class PreferencesRepository : IPreferencesRepository
{
    private readonly NotificationsDbContext _dbContext;

    public PreferencesRepository(NotificationsDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    /// <inheritdoc />
    public Task<NotificationPreference?> GetAsync(string userId, string category, NotificationChannel channel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(category);

        return _dbContext.NotificationPreferences
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId
                                   && p.Category == category
                                   && p.Channel == channel);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<NotificationPreference>> GetAllForUserAsync(string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var rows = await _dbContext.NotificationPreferences
            .AsNoTracking()
            .Where(p => p.UserId == userId)
            .ToListAsync()
            .ConfigureAwait(false);

        return rows;
    }

    /// <inheritdoc />
    public async Task UpsertAsync(NotificationPreference preference)
    {
        ArgumentNullException.ThrowIfNull(preference);

        var existing = await _dbContext.NotificationPreferences
            .FirstOrDefaultAsync(p => p.UserId == preference.UserId
                                   && p.Category == preference.Category
                                   && p.Channel == preference.Channel)
            .ConfigureAwait(false);

        if (existing is null)
        {
            _dbContext.NotificationPreferences.Add(preference);
        }
        else
        {
            // EF tracks the existing row; copy mutable fields onto it via
            // reflection so private setters stay private. The (UserId,
            // Category, Channel) PK is unchanged.
            CopyMutableFields(from: preference, onto: existing);
        }

        await _dbContext.SaveChangesAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<int> GetSendCountAsync(string bucketKey, DateTime windowStart)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bucketKey);

        return _dbContext.RateLimitBuckets
            .AsNoTracking()
            .Where(b => b.BucketKey == bucketKey && b.WindowStart >= windowStart)
            .SumAsync(b => (int?)b.Count)
            .ContinueWith(t => t.Result ?? 0, TaskScheduler.Default);
    }

    private static void CopyMutableFields(NotificationPreference from, NotificationPreference onto)
    {
        var t = typeof(NotificationPreference);

        var isEnabled = t.GetProperty(nameof(NotificationPreference.IsEnabled))!;
        var quietHoursJson = t.GetProperty(nameof(NotificationPreference.QuietHoursJson))!;

        isEnabled.SetValue(onto, from.IsEnabled);
        quietHoursJson.SetValue(onto, from.QuietHoursJson);
    }
}
