using Haworks.Notifications.Domain.Entities;

namespace Haworks.Notifications.Application.Preferences;

public interface IPreferencesRepository
{
    Task<NotificationPreference?> GetAsync(string userId, string category, Domain.Enums.NotificationChannel channel);
    Task UpsertAsync(NotificationPreference preference);
}
