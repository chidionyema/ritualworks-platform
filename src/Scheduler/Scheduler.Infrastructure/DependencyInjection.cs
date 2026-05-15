using Haworks.BuildingBlocks.Messaging;
using Haworks.Scheduler.Application.Common.Interfaces;
using Haworks.Scheduler.Infrastructure.Messaging;
using Haworks.Scheduler.Infrastructure.Persistence;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Hangfire;
using Hangfire.PostgreSql;
using System;

namespace Haworks.Scheduler.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration, IHostEnvironment env)
    {
        var connectionString = configuration.GetConnectionString("scheduler");

        services.AddDbContext<SchedulerDbContext>(options =>
            options.UseNpgsql(connectionString));

        services.AddScoped<IEventScheduler, HangfireEventScheduler>();

        if (Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") != "Test")
        {
            services.AddMassTransit(x =>
            {
                x.SetKebabCaseEndpointNameFormatter();
                x.AddEntityFrameworkOutbox<SchedulerDbContext>(o =>
                {
                    o.UsePostgres();
                    o.UseBusOutbox();
                });

                x.UsingRabbitMq((context, cfg) =>
                {
                    var rabbitMqConfig = configuration.GetSection("RabbitMq");
                    cfg.Host(rabbitMqConfig["Host"], "/", h =>
                    {
                        h.Username(rabbitMqConfig["Username"] ?? throw new InvalidOperationException("RabbitMq:Username is required"));
                        h.Password(rabbitMqConfig["Password"] ?? throw new InvalidOperationException("RabbitMq:Password is required"));
                    });
                    cfg.ConfigureStandardRabbitMq(context);
                });
            });
        }

        services.AddHangfire(config => config
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UsePostgreSqlStorage(options => options.UseNpgsqlConnection(connectionString)));

        services.AddHangfireServer();

        return services;
    }
}
