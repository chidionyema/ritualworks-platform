using Haworks.BuildingBlocks.Messaging;
using Haworks.BuildingBlocks.Vault;
using Haworks.Location.Application.Interfaces;
using Haworks.Location.Infrastructure.Persistence;
using Haworks.Location.Infrastructure.Services;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

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
            services.AddVaultNpgsqlDataSource(connectionString, "haworks-location");
        }

        services.AddDbContext<LocationDbContext>((sp, options) =>
        {
            if (vaultEnabled)
            {
                options.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>(), npgsql =>
                {
                    npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "location");
                    // Enable NetTopologySuite for PostGIS support
                    npgsql.UseNetTopologySuite();
                });
            }
            else
            {
                options.UseNpgsql(connectionString, npgsql =>
                {
                    npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "location");
                    // Enable NetTopologySuite for PostGIS support
                    npgsql.UseNetTopologySuite();
                });
            }
        });

        services.AddScoped<ILocationDbContext>(sp => sp.GetRequiredService<LocationDbContext>());

        // Geospatial services
        services.AddSingleton<IGeohashService, GeohashService>();
        
        services.AddHttpClient<IGeocodingService, NominatimGeocodingService>(c =>
        {
            c.BaseAddress = new Uri("https://nominatim.openstreetmap.org/");
            c.DefaultRequestHeaders.Add("User-Agent", "RitualworksPlatform/1.0");
        });

        if (env.IsEnvironment("Test"))
        {
            services.AddDomainEventPublisher();
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
                cfg.ConfigureStandardRabbitMq(context);
            });
        });

        services.AddDomainEventPublisher();

        return services;
    }
}
