using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Haworks.BuildingBlocks.Messaging;
using Haworks.Catalog.Application.Consumers;
using Haworks.Catalog.Application.Interfaces;
using Haworks.Catalog.Domain.Interfaces;
using Haworks.Catalog.Infrastructure.Caching;
using Haworks.Catalog.Infrastructure.Messaging;
using Haworks.Catalog.Infrastructure.Repositories;
using Haworks.Catalog.Infrastructure.Services;
using System;

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
        services.AddScoped<IProductReviewRepository, ProductReviewRepository>();
        services.AddScoped<IStockService, StockService>();

        // Read-through HybridCache over IProductRepository.GetByIdAsync.
        // Scoped because it captures Scoped IProductRepository — registering
        // as Singleton would be a captive dependency caught at boot under
        // ValidateScopes. The HybridCache instance itself is Singleton
        // (registered in Catalog.Api/Program.cs) so cache state survives
        // across the per-request reader wrappers.
        services.AddScoped<IProductCacheReader, ProductCacheReader>();

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

            // Compensation consumer for the saga's StockReleaseRequested.
            // Anchored to CatalogDbContext outbox via CatalogConsumerDefinition
            // so release + StockReleasedEvent publish commit atomically.
            mt.AddConsumer<StockReleaseRequestedConsumer, CatalogConsumerDefinition<StockReleaseRequestedConsumer>>();

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
