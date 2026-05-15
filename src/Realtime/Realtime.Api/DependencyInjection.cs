using Haworks.Realtime.Api.Application.Common;
using Haworks.Realtime.Api.Application.Notifications;
using Haworks.Realtime.Api.Infrastructure.Persistence;
using Haworks.Realtime.Api.Infrastructure.SignalR;
using Haworks.Realtime.Api.Infrastructure.Messaging;
using Haworks.BuildingBlocks.Behaviors;
using MediatR;
using FluentValidation;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;

namespace Haworks.Realtime.Api;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        var assembly = typeof(DependencyInjection).Assembly;

        services.AddMediatR(cfg => {
            cfg.RegisterServicesFromAssembly(assembly);
            cfg.AddOpenBehavior(typeof(ValidationBehavior<,>));
        });

        services.AddValidatorsFromAssembly(assembly);

        return services;
    }

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSignalR();
        
        // Redis for Inbox and SignalR Backplane (if needed)
        var redisConnectionString = configuration.GetConnectionString("redis") ?? "localhost";
        services.AddStackExchangeRedisCache(options =>
        {
            options.Configuration = redisConnectionString;
        });

        services.AddScoped<IInboxService, RedisInboxService>();

        services.AddMassTransit(x =>
        {
            x.AddConsumer<OrderStatusChangedConsumer>();

            x.UsingRabbitMq((context, cfg) =>
            {
                var rabbitMqHost = configuration.GetConnectionString("rabbitmq") ?? "localhost";
                cfg.Host(rabbitMqHost);
                cfg.ConfigureEndpoints(context);
            });
        });

        return services;
    }
}
