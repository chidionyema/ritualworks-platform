using Haworks.BuildingBlocks.Messaging;
using Haworks.Privacy.Application.Common.Interfaces;
using Haworks.Privacy.Application.Requests.Sagas;
using Haworks.Privacy.Infrastructure.Messaging;
using Haworks.Privacy.Infrastructure.Persistence;
using Haworks.Privacy.Infrastructure.Workers;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Haworks.Privacy.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment env)
    {
        var connectionString = configuration.GetConnectionString("privacy");

        services.AddDbContext<PrivacyDbContext>(options =>
        {
            options.UseNpgsql(connectionString);
            options.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
        });

        services.AddScoped<IPrivacyDbContext>(provider => provider.GetRequiredService<PrivacyDbContext>());

        if (env.IsEnvironment("Test"))
        {
            return services;
        }

        services.AddHostedService<ErasureStalledWatcher>();
        services.AddHostedService<ErasureHealthWatcher>();

        services.AddMassTransit(x =>
        {
            x.SetKebabCaseEndpointNameFormatter();
            x.AddDelayedMessageScheduler();
            x.AddConsumer<Haworks.BuildingBlocks.Messaging.GlobalFaultConsumer>();

            x.AddSagaStateMachine<PrivacyRequestStateMachine, PrivacyRequestState, PrivacyRequestSagaDefinition>()
                .EntityFrameworkRepository(r =>
                {
                    r.ExistingDbContext<PrivacyDbContext>();
                    r.UsePostgres();
                });

            x.AddEntityFrameworkOutbox<PrivacyDbContext>(o =>
            {
                o.UsePostgres();
                o.UseBusOutbox();
                o.QueryDelay = TimeSpan.FromMilliseconds(100);
                o.DuplicateDetectionWindow = TimeSpan.FromMinutes(30);
            });

            x.UsingRabbitMq((context, cfg) =>
            {
                var rabbitConn = configuration.GetConnectionString("rabbitmq")
                    ?? throw new InvalidOperationException("ConnectionStrings:rabbitmq is missing.");

                cfg.Host(new Uri(rabbitConn));
                cfg.UseDelayedMessageScheduler();
                cfg.ConfigureStandardRabbitMq(context);
            });
        });

        return services;
    }
}
