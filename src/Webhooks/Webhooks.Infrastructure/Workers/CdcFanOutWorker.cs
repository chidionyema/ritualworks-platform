using Confluent.Kafka;
using Haworks.Webhooks.Domain;
using Haworks.Webhooks.Application.Interfaces;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hangfire;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Haworks.Webhooks.Infrastructure.Workers;

public class CdcFanOutWorker(
    IConsumer<string, string> consumer,
    IServiceProvider serviceProvider,
    IBackgroundJobClient jobClient,
    ILogger<CdcFanOutWorker> logger) : BackgroundService
{
    private static readonly string[] Topics = 
    [
        "db.catalog.public.products",
        "db.catalog.public.product_categories",
        "db.orders.public.orders",
        "db.payments.public.payments"
    ];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        consumer.Subscribe(Topics);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = consumer.Consume(stoppingToken);
                if (result == null) continue;

                logger.LogInformation("CDC change detected for Webhook Fan-out: {Topic}", result.Topic);

                await ProcessMessageAsync(result, stoppingToken);

                consumer.Commit(result);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error processing CDC webhook fan-out");
            }
        }
    }

    private async Task ProcessMessageAsync(ConsumeResult<string, string> result, CancellationToken ct)
    {
        var message = JsonSerializer.Deserialize<DebeziumEnvelope>(result.Message.Value);
        if (message == null) return;

        using var scope = serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IWebhooksDbContext>();

        var entityType = result.Topic.Split('.').Last();
        var externalEventName = $"{entityType}.{MapOp(message.Op)}";

        // 1. Resolve subscriptions
        var subscriptions = await db.Subscriptions
            .Where(s => s.IsActive && s.DeletedAt == null && s.Events.Contains(externalEventName))
            .ToListAsync(ct);

        if (subscriptions.Count == 0) return;

        // 2. Prepare payload
        var payload = JsonSerializer.Serialize(new
        {
            @event = externalEventName,
            id = $"evt_{DateTime.UtcNow:yyyy-MM-ddTHH-mm-ss}_{Guid.NewGuid().ToString()[..8]}",
            deliveredAt = DateTime.UtcNow,
            data = message.After ?? message.Before
        });

        var deliveries = new List<WebhookDelivery>();
        foreach (var sub in subscriptions)
        {
            var delivery = new WebhookDelivery(sub.Id, result.Message.Key ?? Guid.NewGuid().ToString(), externalEventName, payload);
            db.Deliveries.Add(delivery);
            deliveries.Add(delivery);
        }

        await db.SaveChangesAsync(ct);

        foreach (var delivery in deliveries)
        {
            jobClient.Enqueue<IWebhookDispatcher>(x => x.DispatchAsync(delivery.Id, CancellationToken.None));
        }
    }

    private string MapOp(string op) => op switch
    {
        "c" => "created",
        "u" => "updated",
        "d" => "deleted",
        _ => "changed"
    };

    public record DebeziumEnvelope(
        [property: JsonPropertyName("before")] JsonElement? Before,
        [property: JsonPropertyName("after")] JsonElement? After,
        [property: JsonPropertyName("op")] string Op,
        [property: JsonPropertyName("ts_ms")] long TsMs
    );
}
