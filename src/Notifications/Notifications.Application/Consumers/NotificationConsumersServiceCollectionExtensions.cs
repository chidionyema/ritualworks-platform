using Microsoft.Extensions.DependencyInjection;

namespace Haworks.Notifications.Application;

/// <summary>
/// L3 DI registration. Replaces the L0 stub
/// <c>DependencyInjection.AddNotificationConsumers</c>.
///
/// **No-op now.** The original implementation here called
/// <c>services.AddMassTransit(...)</c> a second time, but MassTransit v8.3+
/// throws <c>ConfigurationException("AddMassTransit() was already called and
/// may only be called once per container")</c> on the second call. The fix
/// is to register the dispatch consumer inside Infrastructure's single
/// <c>AddMassTransit</c> invocation (see
/// <c>Notifications.Infrastructure.DependencyInjection.AddNotificationsInfrastructure</c>).
///
/// This method stays so the call site in the Application composition root
/// keeps compiling; it just doesn't add a second bus registration.
/// </summary>
public static class NotificationConsumersServiceCollectionExtensions
{
    public static IServiceCollection AddNotificationConsumers(this IServiceCollection services)
        => services;
}
