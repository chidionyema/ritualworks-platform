using Microsoft.Extensions.DependencyInjection;

namespace Haworks.Notifications.Application;

/// <summary>
/// L1.C DI registration. Replaces the L0 stub
/// <c>DependencyInjection.AddNotificationPreferences</c>.
/// </summary>
public static class PreferencesServiceCollectionExtensions
{
    public static IServiceCollection AddNotificationPreferences(this IServiceCollection services)
    {
        services.TryAddTimeProviderSingleton();
        services.AddScoped<Notifications.Application.Preferences.IPreferencesService,
                           Notifications.Application.Preferences.PreferencesService>();
        return services;
    }

    private static void TryAddTimeProviderSingleton(this IServiceCollection services)
    {
        // TimeProvider may already be registered by ServiceDefaults; only
        // add a fallback if nothing is wired. Singleton is appropriate
        // because TimeProvider.System is stateless.
        for (var i = 0; i < services.Count; i++)
        {
            if (services[i].ServiceType == typeof(TimeProvider))
            {
                return;
            }
        }
        services.AddSingleton(TimeProvider.System);
    }
}
