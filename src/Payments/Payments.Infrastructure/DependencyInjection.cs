using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Haworks.BuildingBlocks.Messaging;
using Haworks.BuildingBlocks.Caching;
using Haworks.BuildingBlocks.Persistence;
using Haworks.BuildingBlocks.Resilience;
using Haworks.BuildingBlocks.Telemetry;
using Haworks.BuildingBlocks.Vault;
using Haworks.Payments.Application.Consumers;
using Haworks.Payments.Application.Interfaces;
using Haworks.Payments.Application.Sagas;
using Haworks.Payments.Domain;
using Haworks.Payments.Infrastructure.Messaging;
using Haworks.Payments.Infrastructure.Repositories;
using Haworks.Payments.Infrastructure.Stripe;
using Haworks.Payments.Infrastructure.PayPal;
using Haworks.Payments.Infrastructure.Options;
using Haworks.Payments.Infrastructure.Workers;
using Haworks.Contracts.Payments;

namespace Haworks.Payments.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment env)
    {
        var connectionString = configuration.GetConnectionString("payments")
            ?? configuration.GetConnectionString("paymentsdb")
            ?? throw new InvalidOperationException("ConnectionStrings:payments is missing.");

        // Vault integration (gated). When enabled, IVaultService is registered
        // so the DynamicCredentialsConnectionInterceptor can swap the static
        // username/password in the connection string for short-TTL Vault-issued
        // credentials on every connection open. AppRole creds (Vault:RoleId,
        // Vault:SecretId, Vault:SecretIdIsWrapped) come from Fly secrets via
        // ci-stage-vault-creds.sh.
        var vaultEnabled = configuration.GetValue("Vault:Enabled", false)
            && !env.IsEnvironment("Test");
        if (vaultEnabled)
        {
            services.AddVaultIntegration(configuration);
        }

        services.AddDbContext<PaymentDbContext>((sp, options) =>
        {
            options.UseNpgsql(connectionString, npgsql =>
            {
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "payments");
            });

            // Wire dynamic Postgres credential rotation via Vault. Role name
            // matches infra/vault/database/roles.json + the per-service
            // policy granted by deploy/vault/seed.sh. 10-min default TTL —
            // every restart and every renewal cycle gets a fresh ephemeral
            // postgres user; old ones expire at the lease boundary.
            if (vaultEnabled)
            {
                options.AddInterceptors(new DynamicCredentialsConnectionInterceptor(
                    sp.GetRequiredService<IVaultService>(),
                    roleName: "haworks-payments",
                    sp.GetRequiredService<ILogger<DynamicCredentialsConnectionInterceptor>>()));
            }
        });

        services.AddScoped<IPaymentDbContext>(sp => sp.GetRequiredService<PaymentDbContext>());
        services.AddScoped<IPaymentRepository, PaymentRepository>();

        // Cross-cutting BuildingBlocks dependencies. These are also registered
        // by AddVaultIntegration when Vault is enabled, so use TryAdd so we
        // don't double-register and produce a duplicate-singleton warning.
        services.TryAddSingleton<IResiliencePolicyFactory, ResiliencePolicyFactory>();
        services.TryAddSingleton<ITelemetryService>(NullTelemetryService.Instance);

        // Caching
        services.AddHybridCache();
        if (env.IsEnvironment("Test") || env.IsDevelopment())
        {
            services.AddInMemoryDistributedCache();
        }

        // Options
        services.AddOptions<PaymentProviderOptions>()
            .Bind(configuration.GetSection(PaymentProviderOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Stripe registrations
        services.AddSingleton<IStripeClientFactory, StripeClientFactory>();
        services.AddScoped<StripeCheckoutSessionService>();
        services.AddScoped<StripeSubscriptionService>();
        services.AddScoped<StripeSubscriptionManager>();
        services.AddScoped<StripeRefundService>();
        services.AddScoped<StripePaymentSessionCacheService>();
        services.AddScoped<StripePaymentProcessor>();
        services.AddScoped<IWebhookProcessor, StripeWebhookProcessor>();

        // PayPal registrations
        services.AddSingleton<IPayPalClientFactory, PayPalClientFactory>();
        services.AddScoped<PayPalCheckoutService>();
        services.AddScoped<PayPalSubscriptionManager>();
        services.AddScoped<PayPalRefundService>();
        services.AddScoped<PayPalPaymentProcessor>();
        services.AddScoped<IWebhookProcessor, PayPalWebhookProcessor>();

        // Routing & Health
        services.AddScoped<Webhooks.WebhookRouter>();
        services.AddScoped<IWebhookRouter>(sp => sp.GetRequiredService<Webhooks.WebhookRouter>());
        services.AddHealthChecks()
            .AddCheck<Health.PaymentProviderHealthCheck>("payment_provider");

        services.AddScoped<IWebhookIdempotencyGuard, Webhooks.WebhookIdempotencyGuard>();
        services.AddScoped<IPaymentGateway, PaymentGateway>();

        // Interface resolution via Gateway
        services.AddScoped<ICheckoutSessionService>(sp => sp.GetRequiredService<IPaymentGateway>().Checkout);
        services.AddScoped<ISubscriptionManager>(sp => sp.GetRequiredService<IPaymentGateway>().Subscriptions);
        services.AddScoped<IRefundService>(sp => sp.GetRequiredService<IPaymentGateway>().Refunds);
        
        services.AddScoped<IPaymentSessionProcessor>(sp => sp.GetRequiredService<IPaymentGateway>().ActiveProvider switch
        {
            PaymentProvider.Stripe => sp.GetRequiredService<StripePaymentProcessor>(),
            PaymentProvider.PayPal => sp.GetRequiredService<PayPalPaymentProcessor>(),
            _ => throw new NotSupportedException()
        });

        // ISubscriptionService is consumed by CreateSubscriptionCheckoutCommandHandler.
        // Pick the impl matching the active provider — Stripe via its dedicated
        // service, PayPal via the dual-purpose PayPalCheckoutService which
        // implements both ICheckoutSessionService and ISubscriptionService.
        services.AddScoped<ISubscriptionService>(sp => sp.GetRequiredService<IPaymentGateway>().ActiveProvider switch
        {
            PaymentProvider.Stripe => sp.GetRequiredService<StripeSubscriptionService>(),
            PaymentProvider.PayPal => sp.GetRequiredService<PayPalCheckoutService>(),
            _ => throw new NotSupportedException("Active payment provider does not have an ISubscriptionService impl."),
        });

        services.AddScoped<IPaymentSessionCache>(sp => sp.GetRequiredService<IPaymentGateway>().ActiveProvider switch
        {
            PaymentProvider.Stripe => sp.GetRequiredService<StripePaymentSessionCacheService>(),
            _ => throw new NotSupportedException()
        });

        services.AddScoped<IIdempotencyKeyGenerator, Application.Common.IdempotencyKeyGenerator>();
        services.AddScoped<IPaymentAmountMismatchHandler, Application.Webhooks.PaymentAmountMismatchHandler>();

        services.AddHostedService<RefundTimeoutWatcher>();
        services.AddHostedService<SubscriptionRenewalWatcher>();

        services.AddDomainEventPublisher();

        if (env.IsEnvironment("Test"))
        {
            return services;
        }

        services.AddHostedService<SagaHealthWatcher>();

        services.AddMassTransit(mt =>
        {
            mt.SetKebabCaseEndpointNameFormatter();
            mt.AddDelayedMessageScheduler();
            mt.AddEntityFrameworkOutbox<PaymentDbContext>(o =>
            {
                o.UsePostgres();
                o.UseBusOutbox();
                o.DuplicateDetectionWindow = TimeSpan.FromMinutes(30);
            });

            mt.AddSagaStateMachine<RefundSaga, RefundSagaState>()
                .EntityFrameworkRepository(r =>
                {
                    r.ExistingDbContext<PaymentDbContext>();
                    r.UsePostgres();
                });

            mt.AddSagaStateMachine<SubscriptionSaga, SubscriptionSagaState>()
                .EntityFrameworkRepository(r =>
                {
                    r.ExistingDbContext<PaymentDbContext>();
                    r.UsePostgres();
                });

            mt.AddConsumer<PaymentWebhookValidatedConsumer, PaymentsConsumerDefinition<PaymentWebhookValidatedConsumer>>();
            mt.AddConsumer<PaymentSessionRequestedConsumer, PaymentsConsumerDefinition<PaymentSessionRequestedConsumer>>();
            mt.AddConsumer<ProviderRefundInitiationRequestedConsumer, PaymentsConsumerDefinition<ProviderRefundInitiationRequestedConsumer>>();
            mt.AddConsumer<ProviderRefundCancellationConsumer, PaymentsConsumerDefinition<ProviderRefundCancellationConsumer>>();
            mt.AddConsumer<SubscriptionRenewalRequestedConsumer, PaymentsConsumerDefinition<SubscriptionRenewalRequestedConsumer>>();
            mt.AddConsumer<PrivacyErasureRequestedConsumer, PaymentsConsumerDefinition<PrivacyErasureRequestedConsumer>>();

            mt.UsingRabbitMq((context, cfg) =>
            {
                var rabbitConn = configuration.GetConnectionString("rabbitmq")
                    ?? throw new InvalidOperationException();

                cfg.Host(new Uri(rabbitConn));
                cfg.UseDelayedMessageScheduler();
                cfg.UsePublishFilter(typeof(RelayPauseFilter<>), context);
                cfg.ConfigureEndpoints(context);
            });
        });

        return services;
    }
}
