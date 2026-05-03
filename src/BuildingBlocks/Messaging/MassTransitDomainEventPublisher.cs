using MassTransit;
using Microsoft.Extensions.Logging;

namespace Haworks.BuildingBlocks.Messaging;

/// <summary>
/// MassTransit implementation of <see cref="IDomainEventPublisher"/>.
/// Publishes events via <see cref="IPublishEndpoint"/>, which stores them in the outbox
/// when configured with the EF outbox pattern (see <see cref="BoundedContextConsumerDefinition{TConsumer,TDbContext}"/>).
/// </summary>
internal sealed class MassTransitDomainEventPublisher(
    IPublishEndpoint publishEndpoint,
    ILogger<MassTransitDomainEventPublisher> logger) : IDomainEventPublisher
{
    public async Task PublishAsync<TEvent>(TEvent @event, CancellationToken ct = default)
        where TEvent : class
    {
        var actualType = @event.GetType();
        logger.LogDebug("Publishing domain event {EventTypeName} (generic: {GenericTypeName})",
            actualType.Name, typeof(TEvent).Name);

        // Use (object) to force MassTransit to use the runtime type instead of the generic TEvent type.
        // This is crucial when TEvent is a base class or interface.
        await publishEndpoint.Publish((object)@event, ct).ConfigureAwait(false);

        logger.LogInformation("Domain event {EventTypeName} published to outbox", actualType.Name);
    }

    public async Task PublishBatchAsync<TEvent>(IEnumerable<TEvent> events, CancellationToken ct = default)
        where TEvent : class
    {
        var eventList = events.ToList();
        if (eventList.Count == 0) return;

        logger.LogDebug("Publishing batch of {Count} events as objects", eventList.Count);

        foreach (var @event in eventList)
        {
            await publishEndpoint.Publish((object)@event, ct).ConfigureAwait(false);
        }

        logger.LogInformation("Batch of {Count} events published to outbox", eventList.Count);
    }
}
