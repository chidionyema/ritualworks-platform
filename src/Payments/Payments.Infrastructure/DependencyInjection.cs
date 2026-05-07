using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Haworks.BuildingBlocks.Messaging;
using Haworks.Payments.Application.Consumers;
using Haworks.Payments.Application.Interfaces;
using Haworks.Payments.Infrastructure.Messaging;
using Haworks.Payments.Infrastructure.Repositories;
using Haworks.Payments.Infrastructure.Stripe;

namespace Haworks.Payments.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment env)
    {
        var connectionString = configuration.GetConnectionString("payments")
            ?? throw new InvalidOperationException(
                "ConnectionStrings:payments is missing. Aspire injects it via WithReference(paymentsDb).");

        services.AddDbContext<PaymentDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql =>
            {
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "payments");
                // NOTE: EnableRetryOnFailure() is intentionally OFF here.
                // The MT EF outbox installs a SaveChangesInterceptor that
                // serializes deferred publishes into OutboxMessage rows in
                // the same transaction as user changes; Npgsql's retrying
                // execution strategy wraps SaveChanges in its own state
                // machine that breaks that handoff — publishes get dropped
                // silently (no exception, no row, no message at the
                // broker), which is what we observed for T2.5 demo events
                // before this fix. If transient-failure retries become
                // necessary again, do them at the HTTP-handler layer (or
                // wrap operations in db.Database.CreateExecutionStrategy()
                // explicitly) — not at the DbContext options level.
            }));

        services.AddScoped<IPaymentRepository, PaymentRepository>();

        // Stripe registrations
        services.AddOptions<Haworks.Payments.Infrastructure.Options.PaymentProviderOptions>()
            .Bind(configuration.GetSection(Haworks.Payments.Infrastructure.Options.PaymentProviderOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<IStripeClientFactory, Haworks.Payments.Infrastructure.Stripe.StripeClientFactory>();
        services.AddScoped<ICheckoutSessionService, Haworks.Payments.Infrastructure.Stripe.StripeCheckoutSessionService>();
        services.AddScoped<IPaymentSessionCache, Haworks.Payments.Infrastructure.Stripe.StripePaymentSessionCacheService>();
        services.AddScoped<IPaymentSessionProcessor, Haworks.Payments.Infrastructure.Stripe.StripePaymentProcessor>();
        services.AddScoped<IWebhookProcessor, Haworks.Payments.Infrastructure.Stripe.StripeWebhookProcessor>();
        services.AddScoped<IWebhookIdempotencyGuard, Haworks.Payments.Infrastructure.Webhooks.WebhookIdempotencyGuard>();
        services.AddScoped<IPaymentGateway, Haworks.Payments.Infrastructure.PaymentGateway>();
        services.AddScoped<Haworks.Payments.Application.Interfaces.IIdempotencyKeyGenerator, Haworks.Payments.Application.Common.IdempotencyKeyGenerator>();
        services.AddScoped<Haworks.Payments.Application.Interfaces.IPaymentAmountMismatchHandler, Haworks.Payments.Application.Webhooks.PaymentAmountMismatchHandler>();

        // Test fixture supplies its own MassTransit harness + IDomainEventPublisher.
        if (env.IsEnvironment("Test"))
        {
            return services;
        }

        services.AddMassTransit(mt =>
        {
            mt.SetKebabCaseEndpointNameFormatter();

            mt.AddEntityFrameworkOutbox<PaymentDbContext>(o =>
            {
                o.UsePostgres();
                o.UseBusOutbox();
                o.QueryDelay = TimeSpan.FromSeconds(1);
                o.DuplicateDetectionWindow = TimeSpan.FromMinutes(30);
            });

            // Inbox-deduped processing of webhook events. The
            // PaymentsConsumerDefinition wires UseEntityFrameworkOutbox<PaymentDbContext>
            // on the receive endpoint, so consume + state writes + downstream
            // publishes commit atomically in one PaymentDbContext transaction.
            mt.AddConsumer<PaymentWebhookValidatedConsumer, PaymentsConsumerDefinition<PaymentWebhookValidatedConsumer>>();
            mt.AddConsumer<PaymentSessionRequestedConsumer, PaymentsConsumerDefinition<PaymentSessionRequestedConsumer>>();

            mt.UsingRabbitMq((context, cfg) =>
            {
                var rabbitConn = configuration.GetConnectionString("rabbitmq")
                    ?? throw new InvalidOperationException(
                        "ConnectionStrings:rabbitmq is missing. Aspire injects it via WithReference(rabbitmq).");

                cfg.Host(new Uri(rabbitConn));

                // Publish-pipeline filter that gates outbox dispatch on the
                // process-wide RelayPauseGate. Demo /admin/relay-pause flips
                // the flag; failed publishes keep their OutboxMessage rows
                // intact and retry on the next BusOutboxDeliveryService tick.
                // Must be UsePublishFilter (not UseSendFilter): publishes go
                // through the publish pipeline; send is for raw IBus.Send only.
                cfg.UsePublishFilter(typeof(RelayPauseFilter<>), context);

                cfg.ConfigureEndpoints(context);
            });
        });

        services.AddDomainEventPublisher();

        return services;
    }
}
