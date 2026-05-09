using MassTransit;
using Microsoft.Extensions.DependencyInjection;

namespace Haworks.Notifications.Application;

/// <summary>
/// L3 DI registration. Replaces the L0 stub
/// <c>DependencyInjection.AddNotificationConsumers</c>.
///
/// MassTransit's <c>AddConsumer&lt;T&gt;</c> registration is additive across
/// multiple <c>AddMassTransit</c> calls — so registering the dispatch
/// consumer here doesn't conflict with the bus configuration in
/// <c>Notifications.Infrastructure.DependencyInjection</c>.
/// </summary>
public static class NotificationConsumersServiceCollectionExtensions
{
    public static IServiceCollection AddNotificationConsumers(this IServiceCollection services)
    {
        services.AddMassTransit(x =>
        {
            x.AddConsumer<Notifications.Application.Consumers.NotificationRequestConsumer>();
        });
        return services;
    }
}
