using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Haworks.BuildingBlocks.Messaging;
using Haworks.CheckoutOrchestrator.Infrastructure.Messaging;
using Haworks.BuildingBlocks.Persistence;
using Haworks.BuildingBlocks.Vault;
using Haworks.CheckoutOrchestrator.Application.Sagas;

namespace Haworks.CheckoutOrchestrator.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment env)
    {
        var connectionString = configuration.GetConnectionString("checkout")
            ?? throw new InvalidOperationException(
                "ConnectionStrings:checkout is missing. Aspire injects it via WithReference(checkoutDb).");

        // Vault: dynamic Postgres creds via DynamicCredentialsConnectionInterceptor.
        // Role haworks-checkout-orchestrator matches infra/vault/database/roles.json.
        var vaultEnabled = configuration.GetValue("Vault:Enabled", false)
            && !env.IsEnvironment("Test");
        if (vaultEnabled)
        {
            services.AddVaultIntegration(configuration);
        }

        services.AddDbContext<CheckoutDbContext>((sp, options) =>
        {
            options.UseNpgsql(connectionString, npgsql =>
            {
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "checkout");
                npgsql.EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: TimeSpan.FromMilliseconds(500), errorCodesToAdd: null);
            });
            options.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));

            if (vaultEnabled)
            {
                options.AddInterceptors(new DynamicCredentialsConnectionInterceptor(
                    sp.GetRequiredService<IVaultService>(),
                    roleName: "haworks-checkout-orchestrator",
                    sp.GetRequiredService<ILogger<DynamicCredentialsConnectionInterceptor>>()));
            }
        });

        services.AddScoped<Haworks.CheckoutOrchestrator.Application.Interfaces.ICheckoutDbContext>(sp => sp.GetRequiredService<CheckoutDbContext>());

        if (env.IsEnvironment("Test"))
        {
            return services;
        }

        services.AddMassTransit(mt =>
        {
            mt.SetKebabCaseEndpointNameFormatter();

            // In-bus message scheduler — used by CheckoutSaga's PaymentExpiry
            // Schedule to fire a PaymentExpiredEvent 15 min after stock is
            // reserved. Uses the broker's delay mechanism (RabbitMQ delayed-
            // message-exchange plugin) when available.
            mt.AddDelayedMessageScheduler();

            mt.AddEntityFrameworkOutbox<CheckoutDbContext>(o =>
            {
                o.UsePostgres();
                o.UseBusOutbox();
                o.QueryDelay = TimeSpan.FromSeconds(1);
                o.DuplicateDetectionWindow = TimeSpan.FromMinutes(30);
            });

            // CheckoutSaga state machine — persisted to CheckoutDbContext
            // via the EF saga repository. Each receive endpoint for the
            // saga's events runs through UseEntityFrameworkOutbox<CheckoutDbContext>
            // (anchored by CheckoutSagaDefinition), so saga state writes +
            // outbox publishes commit atomically.
            mt.AddSagaStateMachine<CheckoutSaga, CheckoutSagaState, CheckoutSagaDefinition>()
                .EntityFrameworkRepository(r =>
                {
                    r.ExistingDbContext<CheckoutDbContext>();
                    r.UsePostgres();
                });

            mt.UsingRabbitMq((context, cfg) =>
            {
                var rabbitConn = configuration.GetConnectionString("rabbitmq")
                    ?? throw new InvalidOperationException(
                        "ConnectionStrings:rabbitmq is missing.");
                cfg.Host(new Uri(rabbitConn));
                cfg.UseDelayedMessageScheduler();
                cfg.ConfigureStandardRabbitMq(context);
            });
        });

        services.AddDomainEventPublisher();

        // Belt-and-braces fallback for the saga's MT-scheduler-based
        // payment-expiry timeout. Polls every 60s for sagas stuck past
        // 15min and publishes PaymentExpiredEvent directly. Guards
        // against the broker's delayed-message-exchange plugin being
        // missing or silently dropping scheduled messages.
        if (!env.IsEnvironment("Test"))
        {
            services.AddHostedService<Workers.PaymentExpiryWatcher>();
            services.AddHostedService<Workers.SagaHealthWatcher>();
        }

        services.AddOptions<Haworks.CheckoutOrchestrator.Application.Options.CheckoutOptions>()
            .Bind(configuration.GetSection(Haworks.CheckoutOrchestrator.Application.Options.CheckoutOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        return services;
    }
}
