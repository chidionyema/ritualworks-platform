using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using System;

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
    /// Standardizes any MassTransit bus configuration with a baseline retry 
    /// policy (3 attempts, incremental backoff) that applies to all endpoints.
    /// </summary>
    public static void ConfigureStandardBus(
        this IBusFactoryConfigurator cfg,
        IBusRegistrationContext context)
    {
        // Baseline immediate retry for all receive endpoints on the bus.
        cfg.UseMessageRetry(r => r.Incremental(
            retryLimit: 3,
            initialInterval: TimeSpan.FromSeconds(1),
            intervalIncrement: TimeSpan.FromSeconds(2)));

        cfg.ConfigureEndpoints(context);
    }

    /// <summary>
    /// Standardizes the RabbitMQ bus configuration with a baseline retry policy.
    /// Individuals can add more elaborate policies (like delayed redelivery) 
    /// on top of this.
    /// </summary>
    public static void ConfigureStandardRabbitMq(
        this IRabbitMqBusFactoryConfigurator cfg,
        IBusRegistrationContext context)
    {
        cfg.ConfigureStandardBus(context);
    }
}
