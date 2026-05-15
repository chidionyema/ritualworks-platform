using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Haworks.BuildingBlocks.Messaging;
using Haworks.BuildingBlocks.Vault;
using Haworks.Catalog.Application.Consumers;
using Haworks.Catalog.Application.Interfaces;
using Haworks.Catalog.Application.Options;
using Haworks.Catalog.Domain.Interfaces;
using Haworks.Catalog.Infrastructure.BackgroundServices;
using Haworks.Catalog.Infrastructure.Caching;
using Haworks.Catalog.Infrastructure.Messaging;
using Haworks.Catalog.Infrastructure.Metrics;
using Haworks.Catalog.Infrastructure.Repositories;
using Haworks.Catalog.Infrastructure.Services;
using Npgsql;
using System;

namespace Haworks.Catalog.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment env)
    {
        var connectionString = configuration.GetConnectionString("catalog")
            ?? throw new InvalidOperationException(
                "ConnectionStrings:catalog is missing. Aspire injects it via WithReference(catalogDb).");

        // Vault integration — when enabled, the DbContext below uses the
        // NpgsqlDataSource with PeriodicPasswordProvider to swap the static
        // password in the connection string for short-TTL Vault-issued
        // credentials. Role haworks-catalog matches infra/vault/database/roles.json
        // + the per-service policy granted by deploy/vault/seed.sh.
        var vaultEnabled = configuration.GetValue("Vault:Enabled", false)
            && !env.IsEnvironment("Test");
        if (vaultEnabled)
        {
            services.AddVaultIntegration(configuration);
            services.AddVaultNpgsqlDataSource(connectionString, "haworks-catalog");
        }

        services.AddDbContext<CatalogDbContext>((sp, options) =>
        {
            if (vaultEnabled)
            {
                options.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>(), npgsql =>
                    npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "catalog"));
            }
            else
            {
                options.UseNpgsql(connectionString, npgsql =>
                    npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "catalog"));
            }
            options.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
        });

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

        // B3 — reservation sweeper. Options bind from
        // ReservationSweeperOptions.SectionName ("Reservations:Sweeper");
        // defaults match ADR-004 (1 minute / batch of 200). The hosted
        // service itself is registered ONLY outside Test so integration
        // tests can drive SweepOnceAsync deterministically without the
        // timer firing on its own.
        services.AddOptions<ReservationSweeperOptions>()
            .Bind(configuration.GetSection(ReservationSweeperOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<IReservationMetrics, CatalogReservationMetrics>();

        if (!env.IsEnvironment("Test"))
        {
            services.AddHostedService<ReservationSweeperService>();
        }

        // MassTransit + transactional outbox anchored to CatalogDbContext.
        // BusOutboxDeliveryService polls the OutboxMessage table and
        // publishes to RabbitMQ. The ConnectionStrings:rabbitmq URI is
        // injected by Aspire's WithReference(rabbitmq).
        //
        // Skipped in Test environment — the integration fixture wires its
        // own AddMassTransitTestHarness with the in-memory transport so
        // tests can assert publishes synchronously without RabbitMQ.
        if (env.IsEnvironment("Test"))
        {
            // Tests provide their own bus + IDomainEventPublisher; bail early.
            return services;
        }

        services.AddMassTransit(mt =>
        {
            mt.SetKebabCaseEndpointNameFormatter();

            // In-bus message scheduler — required by StockReleaseRequestedConsumer-
            // Definition's UseDelayedRedelivery filter. Uses the broker's delay
            // mechanism (RabbitMQ delayed-message-exchange plugin) when available.
            mt.AddDelayedMessageScheduler();

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

            // Forward consumer for the saga's StockReservationRequested.
            // Closes Phase 4: orchestrator publishes the request, this consumer
            // reserves stock and publishes StockReservedEvent (or Failed),
            // both routed through the outbox for atomicity.
            mt.AddConsumer<StockReservationRequestedConsumer, CatalogConsumerDefinition<StockReservationRequestedConsumer>>();

            // Compensation consumer for the saga's StockReleaseRequested.
            // Anchored to CatalogDbContext outbox via the dedicated
            // StockReleaseRequestedConsumerDefinition which adds 3 immediate
            // retries + 5 delayed redeliveries up to 1h before falling
            // through to the fault topic. Stock release is the saga's last
            // line of defense — if it fails silently, reserved inventory
            // gets stuck.
            mt.AddConsumer<StockReleaseRequestedConsumer, StockReleaseRequestedConsumerDefinition>();

            // Fault observer: catches Fault<StockReleaseRequestedEvent> after
            // all retry/redelivery is exhausted and logs CRITICAL with the
            // orderId + items operators need to unstick the reservation.
            mt.AddConsumer<StockReleaseFaultConsumer>();

            mt.UsingRabbitMq((context, cfg) =>
            {
                var rabbitConn = configuration.GetConnectionString("rabbitmq")
                    ?? throw new InvalidOperationException(
                        "ConnectionStrings:rabbitmq is missing. Aspire injects it via WithReference(rabbitmq).");

                cfg.Host(new Uri(rabbitConn));
                cfg.UseDelayedMessageScheduler();
                cfg.ConfigureStandardRabbitMq(context);
            });
        });

        services.AddDomainEventPublisher();

        return services;
    }
}
