using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Haworks.BuildingBlocks.Messaging;

namespace Haworks.BffWeb.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Phase 7b registers HttpClient typed-clients to backend services.
        // Phase 7c registers MassTransit + the PaymentSessionCreatedConsumer +
        // SignalR. Skipped in Test env where the integration fixture grafts
        // the in-memory MT harness.

        var aspNetEnv = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        if (string.Equals(aspNetEnv, "Test", StringComparison.OrdinalIgnoreCase))
        {
            return services;
        }

        services.AddMassTransit(mt =>
        {
            mt.SetKebabCaseEndpointNameFormatter();

            // Phase 7c: PaymentSessionCreatedConsumer wired here.
            // bff-web has no DbContext + no outbox — it doesn't own state.
            // The consumer is fire-and-forget into SignalR; if the push
            // fails the message is acked (the user can poll the saga
            // status REST endpoint as a fallback).

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

        return services;
    }
}
