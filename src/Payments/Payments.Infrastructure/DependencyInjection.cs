using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Haworks.BuildingBlocks.Messaging;
using Haworks.Payments.Application.Consumers;
using Haworks.Payments.Infrastructure.Messaging;
using Haworks.Payments.Infrastructure.Repositories;

namespace Haworks.Payments.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("payments")
            ?? throw new InvalidOperationException(
                "ConnectionStrings:payments is missing. Aspire injects it via WithReference(paymentsDb).");

        services.AddDbContext<PaymentDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql =>
            {
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "payments");
                // Retry transient Npgsql connection failures (EOF stream,
                // socket timeouts) — bites under Testcontainers on macOS where
                // the docker port-forward occasionally hiccups between scopes.
                npgsql.EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: TimeSpan.FromMilliseconds(500), errorCodesToAdd: null);
            }));

        services.AddScoped<IPaymentRepository, PaymentRepository>();

        // Test fixture supplies its own MassTransit harness + IDomainEventPublisher.
        var aspNetEnv = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        if (string.Equals(aspNetEnv, "Test", StringComparison.OrdinalIgnoreCase))
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
