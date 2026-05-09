using Haworks.Notifications.Application.Suppression;
using Microsoft.Extensions.DependencyInjection;

namespace Haworks.Notifications.Infrastructure;

/// <summary>
/// L1.D DI registration for persistence. Replaces the L0 stub
/// <c>DependencyInjection.AddNotificationSuppressionPersistence</c>.
/// </summary>
public static class SuppressionPersistenceServiceCollectionExtensions
{
    public static IServiceCollection AddNotificationSuppressionPersistence(this IServiceCollection services)
    {
        services.AddScoped<ISuppressionRepository,
                           Persistence.SuppressionStore.SuppressionRepository>();
        return services;
    }
}
