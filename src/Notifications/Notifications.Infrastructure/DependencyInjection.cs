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
        services.AddNotificationSmsChannel();
        services.AddNotificationPushChannel();

        services.AddOptions<Haworks.Notifications.Application.Webhooks.WebhookOptions>()
            .Bind(configuration.GetSection(Haworks.Notifications.Application.Webhooks.WebhookOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        return services;
    }

    internal static IServiceCollection AddNotificationsPersistence(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment env)
    {
        var connectionString = configuration.GetConnectionString("Notifications")
            ?? throw new InvalidOperationException(
                "No notifications database connection string. Expected 'ConnectionStrings:Notifications'.");

        var vaultEnabled = configuration.GetValue("Vault:Enabled", false)
            && !env.IsEnvironment("Test");
        if (vaultEnabled)
        {
            services.AddVaultIntegration(configuration);
            services.AddVaultNpgsqlDataSource(connectionString, "haworks-notifications");
        }

        services.AddScoped<Haworks.Notifications.Application.Commands.INotificationRepository, Haworks.Notifications.Infrastructure.Persistence.NotificationRepository>();

        services.AddDbContext<NotificationsDbContext>((sp, options) =>
        {
            if (vaultEnabled)
            {
                options.UseNpgsql(sp.GetRequiredService<Npgsql.NpgsqlDataSource>(), npgsqlOptions =>
                {
                    npgsqlOptions.MigrationsAssembly(typeof(NotificationsDbContext).Assembly.FullName);
                    npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "notifications");
                });
            }
            else
            {
                options.UseNpgsql(connectionString, npgsqlOptions =>
                {
                    npgsqlOptions.MigrationsAssembly(typeof(NotificationsDbContext).Assembly.FullName);
                    npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "notifications");
                });
            }
        });

        if (!env.IsEnvironment("Test"))
        {
            services.AddMassTransit(x =>
            {
                x.AddConsumer<Notifications.Application.Consumers.NotificationRequestConsumer, Messaging.NotificationsConsumerDefinition<Notifications.Application.Consumers.NotificationRequestConsumer>>();
                x.AddConsumer<Haworks.Notifications.Application.Webhooks.NotificationWebhookValidatedConsumer, Messaging.NotificationsConsumerDefinition<Haworks.Notifications.Application.Webhooks.NotificationWebhookValidatedConsumer>>();
                x.AddConsumer<Notifications.Application.Consumers.RefundEmailConsumer, Messaging.NotificationsConsumerDefinition<Notifications.Application.Consumers.RefundEmailConsumer>>();
                x.AddConsumer<Notifications.Application.Consumers.SecretExpiryWarningConsumer, Messaging.NotificationsConsumerDefinition<Notifications.Application.Consumers.SecretExpiryWarningConsumer>>();

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
                    cfg.ConfigureStandardRabbitMq(context);
                });
            });
        }

        return services;
    }
}
