using Haworks.Notifications.Domain.Entities;
using Haworks.Notifications.Domain.Enums;

namespace Haworks.Notifications.Application.Preferences;

public interface IPreferencesRepository
{
    /// <summary>
    /// Returns the single (UserId, Category, Channel) preference row, or null
    /// if the user has not set an explicit preference for that combination.
    /// </summary>
    Task<NotificationPreference?> GetAsync(string userId, string category, NotificationChannel channel);

    /// <summary>
    /// Returns every preference row for the user — used by the service to
    /// pick up both the per-(category, channel) entry and the global
    /// (category = "*") entry in a single round trip.
    /// </summary>
    Task<IReadOnlyList<NotificationPreference>> GetAllForUserAsync(string userId);

    /// <summary>
    /// Inserts or updates a preference row. Composite PK is
    /// (UserId, Category, Channel).
    /// </summary>
    Task UpsertAsync(NotificationPreference preference);

    /// <summary>
    /// Returns the total <c>Count</c> across <c>RateLimitBuckets</c> rows for
    /// the given <paramref name="bucketKey"/> whose <c>WindowStart</c> is at
    /// or after <paramref name="windowStart"/>. Used by the frequency-cap
    /// check; current implementation collapses all buckets in the lookback
    /// window into a single send count.
    /// </summary>
    Task<int> GetSendCountAsync(string bucketKey, DateTime windowStart);
}
