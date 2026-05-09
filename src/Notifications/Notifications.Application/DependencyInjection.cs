using FluentValidation;
using MediatR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Haworks.BuildingBlocks.Messaging;

namespace Haworks.Notifications.Application;

public static partial class DependencyInjection
{
    public static IServiceCollection AddNotificationsApplication(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddMediatR(cfg => cfg.RegisterServicesFromAssemblyContaining<NotificationsApplicationMarker>());
        services.AddValidatorsFromAssemblyContaining<NotificationsApplicationMarker>();
        services.AddDomainEventPublisher();

        services.AddNotificationTemplates();
        services.AddNotificationPreferences();
        services.AddNotificationSuppressionService();
        services.AddNotificationIdempotency();
        services.AddNotificationConsumers();

        return services;
    }
}

public class NotificationsApplicationMarker { }
