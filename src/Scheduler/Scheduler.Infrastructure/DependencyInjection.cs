using Haworks.Scheduler.Application.Common.Interfaces;
using Haworks.Scheduler.Infrastructure.Messaging;
using Haworks.Scheduler.Infrastructure.Persistence;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Hangfire;
using Hangfire.PostgreSql;

namespace Haworks.Scheduler.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("scheduler");

        services.AddDbContext<SchedulerDbContext>(options =>
            options.UseNpgsql(connectionString));

        services.AddScoped<IEventScheduler, HangfireEventScheduler>();

        services.AddMassTransit(x =>
        {
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
                    h.Username(rabbitMqConfig["Username"] ?? "guest");
                    h.Password(rabbitMqConfig["Password"] ?? "guest");
                });
            });
        });

        services.AddHangfire(config => config
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UsePostgreSqlStorage(options => options.UseNpgsqlConnection(connectionString)));

        services.AddHangfireServer();

        return services;
    }
}
