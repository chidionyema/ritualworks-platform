using Confluent.Kafka;
using Haworks.Contracts.Cdc;
using Haworks.Webhooks.Domain;
using Haworks.Webhooks.Application.Interfaces;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Hangfire;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

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

                try
                {
                    consumer.Commit(result);
                }
                catch (Exception commitEx)
                {
                    logger.LogWarning(commitEx, "Kafka commit failed for {Topic} offset {Offset}; will re-deliver on restart", result.Topic, result.Offset);
                }
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
        DebeziumEnvelope? message;
        try
        {
            message = JsonSerializer.Deserialize<DebeziumEnvelope>(result.Message.Value);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or FormatException or ArgumentNullException)
        {
            logger.LogError(ex, "Skipping malformed CDC message on {Topic} offset {Offset}", result.Topic, result.Offset);
            consumer.Commit(result);
            return;
        }
        if (message == null) return;

        using var scope = serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IWebhooksDbContext>();

        var topicParts = result.Topic.Split('.');
        var entityType = topicParts[topicParts.Length - 1];
        var externalEventName = $"{entityType}.{MapOp(message.Op)}";

        // 1. Resolve subscriptions
        var subscriptions = await db.Subscriptions
            .Where(s => s.IsActive && s.DeletedAt == null && s.Events.Contains(externalEventName))
            .OrderBy(s => s.Id)
            .Take(1000)
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

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            // Duplicate delivery rows — this message was already processed (crash after save, before commit).
            // Log and continue so Kafka offset can be committed; Hangfire jobs are idempotent.
            logger.LogWarning("Duplicate CDC message detected (unique constraint 23505) for topic {Topic} key {Key} — skipping re-insert",
                result.Topic, result.Message.Key);
            return;
        }

        foreach (var delivery in deliveries)
        {
            jobClient.Enqueue<IWebhookDispatcher>(x => x.DispatchAsync(delivery.Id, CancellationToken.None));
        }
    }

    private static string MapOp(string op) => op switch
    {
        "c" => "created",
        "u" => "updated",
        "d" => "deleted",
        _ => "changed"
    };
}
