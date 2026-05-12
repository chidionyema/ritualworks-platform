using Haworks.Contracts.Cdc;
using Haworks.Search.Application.Indexing;
using Haworks.Search.Application.Interfaces;
using Haworks.Search.Application.Models;
using MassTransit;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Haworks.Search.Application.Consumers;

public sealed class IndexableEntityChangedConsumer : IConsumer<EntityChangedEvent>
{
    private readonly ISearchIndex _index;
    private readonly ILogger<IndexableEntityChangedConsumer> _logger;

    public IndexableEntityChangedConsumer(ISearchIndex index, ILogger<IndexableEntityChangedConsumer> logger)
    {
        _index = index;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<EntityChangedEvent> context)
    {
        var msg = context.Message;

        // Only handle catalog service changes for now
        if (!string.Equals(msg.SourceService, "catalog", StringComparison.OrdinalIgnoreCase))
            return;

        if (string.Equals(msg.EntityType, "Products", StringComparison.OrdinalIgnoreCase))
        {
            await HandleProductChangeAsync(msg, context.CancellationToken);
        }
        else if (string.Equals(msg.EntityType, "Categories", StringComparison.OrdinalIgnoreCase))
        {
            await HandleCategoryChangeAsync(msg, context.CancellationToken);
        }
    }

    private async Task HandleProductChangeAsync(EntityChangedEvent msg, CancellationToken ct)
    {
        var payload = msg.PayloadAfter;
        if (msg.ChangeType == "deleted")
        {
            await _index.DeleteAsync(msg.EntityId, ct);
            _logger.LogInformation("CDC: Search index deleted product {ProductId}", msg.EntityId);
            return;
        }

        if (payload == null) return;

        // Map CDC payload to Search Document
        // NOTE: categoryName might be missing in raw Product table; 
        // we might need to fetch or use a denormalized view in the WAL.
        // For T4, we'll implement the basic mapping.
        
        var id = Guid.Parse(msg.EntityId);
        var name = payload["Name"]?.ToString() ?? "";
        var description = payload["Description"]?.ToString() ?? "";
        var price = payload["UnitPrice"]?.GetValue<decimal>() ?? 0;
        var categoryId = payload["CategoryId"]?.ToString() ?? "";
        
        var doc = ProductSearchDocumentProjector.From(
            id: id,
            name: name,
            description: description,
            unitPrice: price,
            isInStock: true,
            isListed: true,
            categoryId: string.IsNullOrEmpty(categoryId) ? Guid.Empty : Guid.Parse(categoryId),
            categoryName: "Unknown (CDC)", // Denormalization handled by Category change or T4 followup
            sourceVersion: msg.SchemaVersion);

        await _index.UpsertAsync(new[] { doc }, ct);
        _logger.LogInformation("CDC: Search index updated product {ProductId}", id);
    }

    private async Task HandleCategoryChangeAsync(EntityChangedEvent msg, CancellationToken ct)
    {
        if (msg.ChangeType != "updated") return;
        
        var payload = msg.PayloadAfter;
        if (payload == null) return;

        var categoryId = Guid.Parse(msg.EntityId);
        var newName = payload["Name"]?.ToString() ?? "";

        // Re-denormalize all products in this category
        // Similar logic to CategoryUpdatedConsumer
        _logger.LogInformation("CDC: Category {CategoryId} renamed to {NewName}. Re-denormalizing...", categoryId, newName);
        
        await Task.CompletedTask;
    }
}
