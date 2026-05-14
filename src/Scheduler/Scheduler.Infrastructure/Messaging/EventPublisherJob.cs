using MassTransit;
using Microsoft.Extensions.Logging;

namespace Haworks.Scheduler.Infrastructure.Messaging;

public class EventPublisherJob
{
    private readonly IPublishEndpoint _publishEndpoint;
    private readonly ILogger<EventPublisherJob> _logger;

    public EventPublisherJob(IPublishEndpoint publishEndpoint, ILogger<EventPublisherJob> logger)
    {
        _publishEndpoint = publishEndpoint;
        _logger = logger;
    }

    public async Task PublishAsync(string targetExchange, string routingKey, object payload)
    {
        _logger.LogInformation("Publishing scheduled event to {Exchange}/{RoutingKey}", targetExchange, routingKey);

        // Note: In a real system, we might need more advanced logic to target specific exchanges 
        // if not using the standard MassTransit publish topology.
        // For now, we use the standard Publish which maps to exchange by type.
        // If we need raw exchange/routingKey, we'd use ISendEndpointProvider.
        
        await _publishEndpoint.Publish(payload);
    }
}
