using Confluent.Kafka;
using Haworks.Contracts.Cdc;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Haworks.BffWeb.Application.Consumers;

/// <summary>
/// Kafka consumer that reads Debezium CDC events from catalog topics
/// and invalidates the BFF's distributed cache for affected entities.
/// </summary>
public sealed class BffCdcCacheInvalidator(
    IConsumer<string, string> consumer,
    IServiceProvider serviceProvider,
    ILogger<BffCdcCacheInvalidator> logger) : BackgroundService
{
    private static readonly string[] Topics =
    [
        "db.catalog.public.products"
    ];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        consumer.Subscribe(Topics);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = consumer.Consume(stoppingToken);
                if (result?.Message?.Value == null) continue;

                await ProcessMessageAsync(result, stoppingToken);
                consumer.Commit(result);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error processing CDC cache invalidation");
            }
        }
    }

    private async Task ProcessMessageAsync(ConsumeResult<string, string> result, CancellationToken ct)
    {
        var envelope = JsonSerializer.Deserialize<DebeziumEnvelope>(result.Message.Value);
        if (envelope == null) return;

        // Extract entity ID from the after payload (or before for deletes)
        var payload = envelope.After ?? envelope.Before;
        if (payload == null) return;

        var entityId = payload.Value.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
        if (entityId == null) return;

        using var scope = serviceProvider.CreateScope();
        var cache = scope.ServiceProvider.GetRequiredService<IDistributedCache>();

        var cacheKey = $"product_detail_{entityId}";
        await cache.RemoveAsync(cacheKey, ct);
        logger.LogInformation("CDC: Invalidated BFF cache for product {ProductId}", entityId);
    }
}
