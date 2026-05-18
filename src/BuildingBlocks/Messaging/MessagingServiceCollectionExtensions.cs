using MassTransit;
using Microsoft.Extensions.DependencyInjection;

namespace Haworks.BuildingBlocks.Messaging;

public static class MessagingServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IDomainEventPublisher"/>. Call this AFTER MassTransit
    /// is wired (the publisher depends on <c>IPublishEndpoint</c>) and AFTER
    /// the per-context outbox is configured so publishes go through the outbox.
    /// </summary>
    public static IServiceCollection AddDomainEventPublisher(this IServiceCollection services)
    {
        services.AddScoped<IDomainEventPublisher, MassTransitDomainEventPublisher>();
        return services;
    }

    /// <summary>
    /// Standardizes the RabbitMQ bus configuration with a baseline retry policy
    /// (3 attempts, incremental backoff) that applies to all endpoints.
    /// </summary>
    public static void ConfigureStandardRabbitMq(
        this IRabbitMqBusFactoryConfigurator cfg,
        IBusRegistrationContext context)
    {
        // Stage 1: Immediate retries (transient blips — network glitch, brief DB lock)
        cfg.UseMessageRetry(r => r.Incremental(
            retryLimit: 3,
            initialInterval: TimeSpan.FromSeconds(1),
            intervalIncrement: TimeSpan.FromSeconds(2)));

        // Stage 2: Delayed redelivery (service outages — Stripe down, DB failover)
        // After 3 immediate retries fail, message is redelivered 3 more times with
        // longer delays. Total time before DLQ: ~36 min (vs 9s without this).
        // Requires: rabbitmq_delayed_message_exchange plugin enabled on broker.
        cfg.UseDelayedRedelivery(r => r.Intervals(
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(30)));

        cfg.ConfigureEndpoints(context);
    }
}
