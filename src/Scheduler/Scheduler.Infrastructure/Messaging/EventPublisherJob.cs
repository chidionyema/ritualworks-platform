using System;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace Haworks.Scheduler.Infrastructure.Messaging;

public class EventPublisherJob
{
    private readonly ISendEndpointProvider _sendEndpointProvider;
    private readonly ILogger<EventPublisherJob> _logger;

    public EventPublisherJob(ISendEndpointProvider sendEndpointProvider, ILogger<EventPublisherJob> logger)
    {
        _sendEndpointProvider = sendEndpointProvider ?? throw new ArgumentNullException(nameof(sendEndpointProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    private static readonly Regex SafeNamePattern =
        new(@"^[a-zA-Z0-9._:\-]{1,255}$", RegexOptions.Compiled | RegexOptions.NonBacktracking);

    public async Task PublishAsync(string targetExchange, string routingKey, string payload, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(targetExchange))
            throw new ArgumentException("targetExchange must not be empty", nameof(targetExchange));
        if (!SafeNamePattern.IsMatch(targetExchange))
            throw new ArgumentException(
                "targetExchange contains invalid characters. Allowed: a-z A-Z 0-9 . _ : -",
                nameof(targetExchange));
        if (!string.IsNullOrEmpty(routingKey) && !SafeNamePattern.IsMatch(routingKey))
            throw new ArgumentException(
                "routingKey contains invalid characters. Allowed: a-z A-Z 0-9 . _ : -",
                nameof(routingKey));
        if (string.IsNullOrWhiteSpace(payload))
            throw new ArgumentException("payload must not be empty", nameof(payload));

        _logger.LogInformation("Publishing scheduled event to exchange={Exchange} routingKey={RoutingKey}", targetExchange, routingKey);

        // Build a RabbitMQ exchange URI so MassTransit routes to the exact exchange/routing-key
        // rather than using the generic publish topology.
        var exchangeUri = string.IsNullOrWhiteSpace(routingKey)
            ? new Uri($"rabbitmq://{targetExchange}")
            : new Uri($"rabbitmq://{targetExchange}/{routingKey}");

        var endpoint = await _sendEndpointProvider.GetSendEndpoint(exchangeUri).ConfigureAwait(false);

        // Deserialize the raw JSON payload to object so MassTransit can serialise it
        // with full header metadata (MessageId, CorrelationId, etc.).
        // The payload was stored as raw JSON string to survive Hangfire serialization intact.
        using var document = JsonDocument.Parse(payload);
        var envelope = document.RootElement.Clone();

        await endpoint.Send<object>(envelope, ctx =>
        {
            ctx.Headers.Set("X-Scheduler-Exchange", targetExchange);
            ctx.Headers.Set("X-Scheduler-RoutingKey", routingKey ?? string.Empty);
        }, ct).ConfigureAwait(false);
    }
}
