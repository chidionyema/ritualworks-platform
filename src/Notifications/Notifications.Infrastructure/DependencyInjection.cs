using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Haworks.Notifications.Infrastructure.Persistence;
using Haworks.BuildingBlocks.Messaging;
using Haworks.BuildingBlocks.Persistence;
using Haworks.BuildingBlocks.Vault;
using MassTransit;

namespace Haworks.Notifications.Infrastructure;

public static partial class DependencyInjection
{
    public static IServiceCollection AddNotificationsInfrastructure(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment env)
    {
        services.AddNotificationsPersistence(configuration, env);

        services.AddNotificationTemplatesPersistence();
        services.AddNotificationPreferencesPersistence();
        services.AddNotificationSuppressionPersistence();
        services.AddSesEmailProvider(configuration);
        services.AddSendGridEmailProvider(configuration);
        services.AddTwilioSmsProvider(configuration);
        services.AddFcmPushProvider(configuration);
        services.AddNotificationChannelGateways();
        services.AddNotificationPushChannel();

        return services;
    }

    internal static IServiceCollection AddNotificationsPersistence(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment env)
    {
        var connectionString = configuration.GetConnectionString("Notifications");

        services.AddDbContext<NotificationsDbContext>(options =>
            options.UseNpgsql(connectionString, npgsqlOptions =>
            {
                npgsqlOptions.MigrationsAssembly(typeof(NotificationsDbContext).Assembly.FullName);
                npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "notifications");
            }));

        services.AddMassTransit(x =>
        {
            // Register the dispatch consumer here, inside the SINGLE
            // AddMassTransit call. MT v8 throws ConfigurationException on a
            // second AddMassTransit call per container — so the L3 extension
            // method (NotificationConsumersServiceCollectionExtensions) is
            // a no-op now; we wire the consumer directly here. Application
            // owns the consumer type; Infrastructure owns the bus.
            x.AddConsumer<Notifications.Application.Consumers.NotificationRequestConsumer>();

            x.AddEntityFrameworkOutbox<NotificationsDbContext>(o =>
            {
                o.UsePostgres();
                o.UseBusOutbox();
            });

            x.UsingRabbitMq((context, cfg) =>
            {
                var rabbitConn = configuration.GetConnectionString("RabbitMQ")
                    ?? throw new InvalidOperationException("RabbitMQ connection string missing");

                cfg.Host(new Uri(rabbitConn));
                cfg.ConfigureEndpoints(context);
            });
        });

        return services;
    }
}
