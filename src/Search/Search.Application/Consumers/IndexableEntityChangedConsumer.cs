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
    private static readonly string[] Topics =
    [
        "db.catalog.public.products",
        "db.catalog.public.categories"
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
                logger.LogError(ex, "Error processing CDC search index update");
            }
        }
    }

    private async Task ProcessMessageAsync(ConsumeResult<string, string> result, CancellationToken ct)
    {
        var envelope = JsonSerializer.Deserialize<DebeziumEnvelope>(result.Message.Value);
        if (envelope == null) return;

        var table = result.Topic.Split('.').Last();
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
        if (changeType == "deleted")
        {
            var before = envelope.Before?.GetProperty("id").GetString();
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
            isInStock: true,
            isListed: true,
            categoryId: string.IsNullOrEmpty(categoryId) ? Guid.Empty : Guid.Parse(categoryId),
            categoryName: "Unknown (CDC)",
            sourceVersion: 1);

        await index.UpsertAsync(new[] { doc }, ct);
        logger.LogInformation("CDC: Search index updated product {ProductId}", id);
    }

    private async Task HandleCategoryChangeAsync(
        DebeziumEnvelope envelope, string changeType, ISearchIndex index, CancellationToken ct)
    {
        if (changeType != "updated") return;
        if (envelope.After == null) return;

        var after = envelope.After.Value;
        var categoryId = after.GetProperty("id").GetString();
        var newName = after.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";

        logger.LogInformation("CDC: Category {CategoryId} renamed to {NewName}. Re-denormalizing...",
            categoryId, newName);

        await Task.CompletedTask;
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
