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
}
