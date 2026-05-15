using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Haworks.BuildingBlocks.Messaging;
using Haworks.BuildingBlocks.Persistence;
using Haworks.BuildingBlocks.Vault;
using Haworks.Orders.Application.Consumers;
using Haworks.Orders.Infrastructure.Messaging;
using Haworks.Orders.Infrastructure.Repositories;

namespace Haworks.Orders.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment env)
    {
        var connectionString = configuration.GetConnectionString("orders")
            ?? throw new InvalidOperationException(
                "ConnectionStrings:orders is missing. Aspire injects it via WithReference(ordersDb).");

        // Vault: dynamic Postgres creds via DynamicCredentialsConnectionInterceptor.
        // Role haworks-orders matches infra/vault/database/roles.json.
        var vaultEnabled = configuration.GetValue("Vault:Enabled", false)
            && !env.IsEnvironment("Test");
        if (vaultEnabled)
        {
            services.AddVaultIntegration(configuration);
        }

        services.AddDbContext<OrderDbContext>((sp, options) =>
        {
            options.UseNpgsql(connectionString, npgsql =>
            {
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "orders");
                // EF retry-on-failure mitigates the macOS Docker / Npgsql 9
                // EOF stream flake first observed in payments-svc Phase 3 — see
                // docs/runbooks/payments-integration-docker-flake.md.
                npgsql.EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: TimeSpan.FromMilliseconds(500), errorCodesToAdd: null);
            });

            if (vaultEnabled)
            {
                options.AddInterceptors(new DynamicCredentialsConnectionInterceptor(
                    sp.GetRequiredService<IVaultService>(),
                    roleName: "haworks-orders",
                    sp.GetRequiredService<ILogger<DynamicCredentialsConnectionInterceptor>>()));
            }
        });

        services.AddScoped<IOrderRepository, OrderRepository>();

        // Test fixture wires its own MassTransit harness + IDomainEventPublisher.
        if (env.IsEnvironment("Test"))
        {
            return services;
        }

        services.AddMassTransit(mt =>
        {
            mt.SetKebabCaseEndpointNameFormatter();

            mt.AddEntityFrameworkOutbox<OrderDbContext>(o =>
            {
                o.UsePostgres();
                o.UseBusOutbox();
                o.QueryDelay = TimeSpan.FromSeconds(1);
                o.DuplicateDetectionWindow = TimeSpan.FromMinutes(30);
            });

            // Cross-context consumers. Each is anchored to OrderDbContext via
            // OrdersConsumerDefinition<T> so consume + state writes + downstream
            // publishes commit atomically in one OrderDbContext transaction.
            mt.AddConsumer<PaymentCompletedConsumer, OrdersConsumerDefinition<PaymentCompletedConsumer>>();
            mt.AddConsumer<PaymentSessionFailedConsumer, OrdersConsumerDefinition<PaymentSessionFailedConsumer>>();
            mt.AddConsumer<StockReservationFailedConsumer, OrdersConsumerDefinition<StockReservationFailedConsumer>>();
            mt.AddConsumer<CheckoutSessionExpiredConsumer, OrdersConsumerDefinition<CheckoutSessionExpiredConsumer>>();
            mt.AddConsumer<RefundCompletedConsumer, OrdersConsumerDefinition<RefundCompletedConsumer>>();
            mt.AddConsumer<RefundCancelledConsumer, OrdersConsumerDefinition<RefundCancelledConsumer>>();
            mt.AddConsumer<PrivacyErasureRequestedConsumer, OrdersConsumerDefinition<PrivacyErasureRequestedConsumer>>();

            mt.UsingRabbitMq((context, cfg) =>
            {
                var rabbitConn = configuration.GetConnectionString("rabbitmq")
                    ?? throw new InvalidOperationException(
                        "ConnectionStrings:rabbitmq is missing. Aspire injects it via WithReference(rabbitmq).");
                cfg.Host(new Uri(rabbitConn));
                cfg.ConfigureStandardRabbitMq(context);
            });
        });

        services.AddDomainEventPublisher();

        return services;
    }
}
