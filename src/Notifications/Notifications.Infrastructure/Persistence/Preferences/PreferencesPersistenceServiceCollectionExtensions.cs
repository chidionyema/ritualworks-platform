using Haworks.Notifications.Application.Preferences;
using Microsoft.Extensions.DependencyInjection;

namespace Haworks.Notifications.Infrastructure;

/// <summary>
/// L1.C DI registration for persistence. Replaces the L0 stub
/// <c>DependencyInjection.AddNotificationPreferencesPersistence</c>.
/// </summary>
public static class PreferencesPersistenceServiceCollectionExtensions
{
    public static IServiceCollection AddNotificationPreferencesPersistence(this IServiceCollection services)
    {
        services.AddScoped<IPreferencesRepository,
                           Persistence.PreferencesStore.PreferencesRepository>();
        return services;
    }
}
