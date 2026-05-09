using Microsoft.Extensions.DependencyInjection;

namespace Haworks.Notifications.Application;

/// <summary>
/// L1.D DI registration. Replaces the L0 stub
/// <c>DependencyInjection.AddNotificationSuppressionService</c>.
/// </summary>
public static class SuppressionServiceCollectionExtensions
{
    public static IServiceCollection AddNotificationSuppressionService(this IServiceCollection services)
    {
        services.AddScoped<Notifications.Application.Suppression.ISuppressionService,
                           Notifications.Application.Suppression.SuppressionService>();
        return services;
    }
}
