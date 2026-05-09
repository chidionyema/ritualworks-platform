using Haworks.Notifications.Domain.Enums;

namespace Haworks.Notifications.Application.Preferences;

public interface IPreferencesService
{
    Task<PreferenceCheckResult> IsAllowedAsync(string userId, NotificationChannel channel, string category, CancellationToken ct);
}

public enum PreferenceCheckResult
{
    Allow,
    Suppressed,
    RateLimited,
    QuietHours
}
