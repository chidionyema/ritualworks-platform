using Haworks.BuildingBlocks.Behaviors;
using Haworks.BuildingBlocks.Messaging;
using Haworks.Realtime.Api.Application.Common;
using Haworks.Realtime.Api.Infrastructure.Messaging;
using Haworks.Realtime.Api.Infrastructure.Persistence;
using Haworks.Realtime.Api.Infrastructure.SignalR;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace Haworks.Realtime.Api;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        var assembly = typeof(DependencyInjection).Assembly;

        services.AddMediatR(cfg =>
        {
            cfg.RegisterServicesFromAssembly(assembly);
            cfg.AddOpenBehavior(typeof(TelemetryBehavior<,>));
            cfg.AddOpenBehavior(typeof(ValidationBehavior<,>));
        });

        services.AddValidatorsFromAssembly(assembly);

        return services;
    }

    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment env)
    {
        // Redis — shared IConnectionMultiplexer for atomic Lua scripts.
        var redisConn = configuration.GetConnectionString("Redis")
            ?? throw new InvalidOperationException("Redis connection string is required.");

        services.AddSingleton<IConnectionMultiplexer>(_ =>
            ConnectionMultiplexer.Connect(redisConn));

        // Inbox service backed by Redis LIST operations.
        services.AddSingleton<IInboxService, RedisInboxService>();

        // SignalR with Redis backplane for multi-instance scale-out.
        services.AddSignalR()
                .AddStackExchangeRedis(redisConn);

        // Distributed cache (IDistributedCache) for any other cache consumers.
        services.AddStackExchangeRedisCache(opts => opts.Configuration = redisConn);

        // MassTransit — RabbitMQ in all environments except Test.
        if (!env.IsEnvironment("Test"))
        {
            services.AddMassTransit(x =>
            {
                x.AddConsumer<OrderStatusChangedConsumer>();

                x.UsingRabbitMq((context, cfg) =>
                {
                    var rabbitConn = configuration.GetConnectionString("RabbitMQ")
                        ?? throw new InvalidOperationException("RabbitMQ connection string is required.");

                    cfg.Host(new Uri(rabbitConn));
                    cfg.ConfigureStandardRabbitMq(context);
                });
            });
        }

        // Health checks — Redis readiness probe using IConnectionMultiplexer.
        services.AddHealthChecks()
                .AddCheck("redis", sp =>
                {
                    try
                    {
                        var mux = sp.GetRequiredService<IConnectionMultiplexer>();
                        return mux.IsConnected
                            ? Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy()
                            : Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Unhealthy("Redis not connected");
                    }
                    catch (Exception ex)
                    {
                        return Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Unhealthy(ex.Message);
                    }
                }, tags: ["ready"]);

        return services;
    }
}
