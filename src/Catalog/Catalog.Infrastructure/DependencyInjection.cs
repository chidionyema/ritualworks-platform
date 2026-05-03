using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Haworks.BuildingBlocks.Messaging;
using Haworks.Catalog.Infrastructure.Repositories;

namespace Haworks.Catalog.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("catalog")
            ?? throw new InvalidOperationException(
                "ConnectionStrings:catalog is missing. Aspire injects it via WithReference(catalogDb).");

        services.AddDbContext<CatalogDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "catalog")));

        services.AddScoped<IProductRepository, ProductRepository>();
        services.AddScoped<ICategoryRepository, CategoryRepository>();

        // MassTransit + transactional outbox anchored to CatalogDbContext.
        // BusOutboxDeliveryService polls the OutboxMessage table and
        // publishes to RabbitMQ. The ConnectionStrings:rabbitmq URI is
        // injected by Aspire's WithReference(rabbitmq).
        //
        // Skipped in Test environment — the integration fixture wires its
        // own AddMassTransitTestHarness with the in-memory transport so
        // tests can assert publishes synchronously without RabbitMQ.
        var aspNetEnv = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        if (string.Equals(aspNetEnv, "Test", StringComparison.OrdinalIgnoreCase))
        {
            // Tests provide their own bus + IDomainEventPublisher; bail early.
            return services;
        }

        services.AddMassTransit(mt =>
        {
            mt.SetKebabCaseEndpointNameFormatter();

            // Producer-side outbox: PublishEndpoint.Publish(...) writes to
            // OutboxMessage instead of going straight to the broker. The row
            // commits with the surrounding business transaction.
            mt.AddEntityFrameworkOutbox<CatalogDbContext>(o =>
            {
                o.UsePostgres();
                o.UseBusOutbox();
                o.QueryDelay = TimeSpan.FromSeconds(1);
                o.DuplicateDetectionWindow = TimeSpan.FromMinutes(30);
            });

            mt.UsingRabbitMq((context, cfg) =>
            {
                var rabbitConn = configuration.GetConnectionString("rabbitmq")
                    ?? throw new InvalidOperationException(
                        "ConnectionStrings:rabbitmq is missing. Aspire injects it via WithReference(rabbitmq).");

                cfg.Host(new Uri(rabbitConn));
                cfg.ConfigureEndpoints(context);
            });
        });

        services.AddDomainEventPublisher();

        return services;
    }
}
