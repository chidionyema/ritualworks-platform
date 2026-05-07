using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Haworks.BuildingBlocks.Messaging;
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

        services.AddDbContext<CheckoutDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql =>
            {
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "checkout");
                npgsql.EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: TimeSpan.FromMilliseconds(500), errorCodesToAdd: null);
            }));

        if (env.IsEnvironment("Test"))
        {
            return services;
        }

        services.AddMassTransit(mt =>
        {
            mt.SetKebabCaseEndpointNameFormatter();

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
            // (anchored by BoundedContextSagaDefinition in BuildingBlocks),
            // so saga state writes + outbox publishes commit atomically.
            mt.AddSagaStateMachine<CheckoutSaga, CheckoutSagaState>()
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
                cfg.ConfigureEndpoints(context);
            });
        });

        services.AddDomainEventPublisher();

        services.AddOptions<Haworks.CheckoutOrchestrator.Application.Options.CheckoutOptions>()
            .Bind(configuration.GetSection(Haworks.CheckoutOrchestrator.Application.Options.CheckoutOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        return services;
    }
}
