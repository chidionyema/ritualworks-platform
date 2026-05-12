using Haworks.BuildingBlocks.Messaging;
using Haworks.BuildingBlocks.Persistence;
using Haworks.BuildingBlocks.Vault;
using Haworks.Location.Infrastructure.Persistence;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Haworks.Location.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment env)
    {
        var connectionString = configuration.GetConnectionString("location")
            ?? throw new InvalidOperationException(
                "ConnectionStrings:location is missing. Aspire injects it via WithReference(locationDb).");

        var vaultEnabled = configuration.GetValue("Vault:Enabled", false)
            && !env.IsEnvironment("Test");
            
        if (vaultEnabled)
        {
            services.AddVaultIntegration(configuration);
        }

        services.AddDbContext<LocationDbContext>((sp, options) =>
        {
            options.UseNpgsql(connectionString, npgsql =>
            {
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "location");
                // Enable NetTopologySuite for PostGIS support
                npgsql.UseNetTopologySuite();
            });

            if (vaultEnabled)
            {
                options.AddInterceptors(new DynamicCredentialsConnectionInterceptor(
                    sp.GetRequiredService<IVaultService>(),
                    roleName: "haworks-location",
                    sp.GetRequiredService<ILogger<DynamicCredentialsConnectionInterceptor>>()));
            }
        });

        if (env.IsEnvironment("Test"))
        {
            return services;
        }

        services.AddMassTransit(mt =>
        {
            mt.SetKebabCaseEndpointNameFormatter();

            mt.AddEntityFrameworkOutbox<LocationDbContext>(o =>
            {
                o.UsePostgres();
                o.UseBusOutbox();
                o.QueryDelay = TimeSpan.FromSeconds(1);
            });

            mt.UsingRabbitMq((context, cfg) =>
            {
                var rabbitConn = configuration.GetConnectionString("rabbitmq")
                    ?? throw new InvalidOperationException("ConnectionStrings:rabbitmq is missing.");

                cfg.Host(new Uri(rabbitConn));
                cfg.ConfigureEndpoints(context);
            });
        });

        services.AddDomainEventPublisher();

        return services;
    }
}
