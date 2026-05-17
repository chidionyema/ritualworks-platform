using System.Diagnostics.Metrics;
using Confluent.Kafka;
using Haworks.Contracts.Cdc;
using Haworks.Search.Application.Indexing;
using Haworks.Search.Application.Interfaces;
using Haworks.Search.Application.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Haworks.Search.Application.Consumers;

/// <summary>
/// Kafka consumer that reads Debezium CDC events from catalog topics
/// and updates the Elasticsearch search index accordingly.
/// </summary>
public sealed class CdcSearchIndexWorker(
    IConsumer<string, string> consumer,
    IServiceProvider serviceProvider,
    ILogger<CdcSearchIndexWorker> logger) : BackgroundService
{
    private static readonly Meter Meter = new("Haworks.Search", "1.0.0");
    private static readonly Counter<long> MessagesProcessed = Meter.CreateCounter<long>(
        "kafka.consumer.messages.processed", description: "CDC messages processed");
    private static readonly Counter<long> MessagesSkipped = Meter.CreateCounter<long>(
        "kafka.consumer.messages.skipped", description: "CDC messages skipped (malformed)");

    private long _lagMessages;
    private static readonly ObservableGauge<long> LagGauge = Meter.CreateObservableGauge(
        "kafka.consumer.lag.messages",
        () => []);

    private static readonly string[] Topics =
    [
        "db.catalog.public.products",
        "db.catalog.public.categories"
    ];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        consumer.Subscribe(Topics);

        // Register lag gauge with instance reference
        Meter.CreateObservableGauge("kafka.consumer_lag_messages",
            () => new Measurement<long>(_lagMessages,
                new KeyValuePair<string, object?>("group", "search-svc-cdc")),
            description: "Kafka consumer lag in messages");

        ConsumeResult<string, string>? result = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                result = consumer.Consume(stoppingToken);
                if (result?.Message?.Value == null) continue;

                await ProcessMessageAsync(result, stoppingToken);
                consumer.Commit(result);
                MessagesProcessed.Add(1, new KeyValuePair<string, object?>("topic", result.Topic));

                // Update lag estimate from partition watermarks
                UpdateLagEstimate(result);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or KeyNotFoundException or FormatException or ArgumentNullException)
            {
                logger.LogError(ex, "Skipping malformed CDC message on {Topic}", result?.Topic);
                if (result != null) consumer.Commit(result);
                MessagesSkipped.Add(1, new KeyValuePair<string, object?>("topic", result?.Topic ?? "unknown"));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error processing CDC search index update");
            }
        }

        consumer.Close();
    }

    private void UpdateLagEstimate(ConsumeResult<string, string> result)
    {
        try
        {
            var watermarks = consumer.QueryWatermarkOffsets(result.TopicPartition, TimeSpan.FromSeconds(1));
            _lagMessages = watermarks.High.Value - result.Offset.Value;
        }
        catch
        {
            // Non-critical — lag metric will be stale but won't crash the consumer
        }
    }

    private async Task ProcessMessageAsync(ConsumeResult<string, string> result, CancellationToken ct)
    {
        var envelope = JsonSerializer.Deserialize<DebeziumEnvelope>(result.Message.Value);
        if (envelope == null) return;

        var topicParts = result.Topic.Split('.');
        var table = topicParts[topicParts.Length - 1];
        var changeType = MapOp(envelope.Op);

        using var scope = serviceProvider.CreateScope();
        var index = scope.ServiceProvider.GetRequiredService<ISearchIndex>();

        if (string.Equals(table, "products", StringComparison.OrdinalIgnoreCase))
        {
            await HandleProductChangeAsync(envelope, changeType, index, ct);
        }
        else if (string.Equals(table, "categories", StringComparison.OrdinalIgnoreCase))
        {
            await HandleCategoryChangeAsync(envelope, changeType, index, ct);
        }
    }

    private async Task HandleProductChangeAsync(
        DebeziumEnvelope envelope, string changeType, ISearchIndex index, CancellationToken ct)
    {
        if (string.Equals(changeType, "deleted", StringComparison.Ordinal))
        {
            var beforeRaw = envelope.Before?.GetProperty("id").GetString();
            var before = beforeRaw != null && Guid.TryParse(beforeRaw, out var parsedId)
                ? parsedId.ToString("N")
                : beforeRaw;
            if (before != null)
            {
                await index.DeleteAsync(before, ct);
                logger.LogInformation("CDC: Search index deleted product {ProductId}", before);
            }
            return;
        }

        if (envelope.After == null) return;
        var after = envelope.After.Value;

        var id = Guid.Parse(after.GetProperty("id").GetString()!);
        var name = after.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        var description = after.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
        var price = after.TryGetProperty("unit_price", out var p) ? p.GetDecimal() : 0;
        var categoryId = after.TryGetProperty("category_id", out var c) ? c.GetString() ?? "" : "";

        var doc = ProductSearchDocumentProjector.From(
            id: id,
            name: name,
            description: description,
            unitPrice: price,
            isInStock: after.TryGetProperty("is_in_stock", out var stockProp) && stockProp.ValueKind == System.Text.Json.JsonValueKind.True,
            isListed: after.TryGetProperty("is_listed", out var listedProp) && listedProp.ValueKind == System.Text.Json.JsonValueKind.True,
            categoryId: string.IsNullOrEmpty(categoryId) ? Guid.Empty : Guid.Parse(categoryId),
            categoryName: "Unknown (CDC)",
            sourceVersion: 1);

        await index.UpsertAsync(new[] { doc }, ct);
        logger.LogInformation("CDC: Search index updated product {ProductId}", id);
    }

    private async Task HandleCategoryChangeAsync(
        DebeziumEnvelope envelope, string changeType, ISearchIndex index, CancellationToken ct)
    {
        if (!string.Equals(changeType, "updated", StringComparison.Ordinal)) return;
        if (envelope.After == null) return;

        var after = envelope.After.Value;
        var categoryIdStr = after.GetProperty("id").GetString();
        var newName = after.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";

        if (string.IsNullOrEmpty(categoryIdStr) || !Guid.TryParse(categoryIdStr, out var categoryId))
        {
            logger.LogWarning("CDC: Category change skipped — invalid categoryId '{Raw}'", categoryIdStr);
            return;
        }

        logger.LogInformation("CDC: Category {CategoryId} renamed to {NewName}. Re-denormalizing...",
            categoryId, newName);

        const int batchSize = 1000;
        var totalUpdated = 0;
        var page = 1;

        while (true)
        {
            var hits = await index.SearchAsync(new Models.SearchQuery
            {
                Query = "",
                CategoryFilter = categoryId,
                Page = page,
                PageSize = batchSize,
            }, ct).ConfigureAwait(false);

            if (hits.Hits.Count == 0) break;

            var renamed = hits.Hits.Select(h => h with { CategoryName = newName }).ToList();
            await index.UpsertAsync(renamed, ct).ConfigureAwait(false);
            totalUpdated += renamed.Count;

            if (hits.Hits.Count < batchSize) break;
            page++;
        }

        logger.LogInformation("CDC: Re-denormalised categoryName for {Count} products in category {CategoryId}",
            totalUpdated, categoryId);
    }

    private static string MapOp(string op) => op switch
    {
        "c" => "created",
        "r" => "created", // snapshot read
        "u" => "updated",
        "d" => "deleted",
        _ => "changed"
    };
}
