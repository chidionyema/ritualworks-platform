using Haworks.Contracts.Catalog;
using Haworks.Search.Application.Catalog;
using Haworks.Search.Application.Indexing;
using Haworks.Search.Application.Interfaces;
using Haworks.Search.Application.Models;
using Haworks.Search.Application.Telemetry;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace Haworks.Search.Application.Consumers;

// Public so MassTransit's reflection over assembly types finds it across
// project boundaries (Application is a sibling of Infrastructure). Other
// consumers in this codebase follow the same convention.
public sealed class ProductCacheInvalidatedConsumer : IConsumer<ProductCacheInvalidatedEvent>
{
    private readonly ISearchIndex _index;
    private readonly ICatalogProductsApi _catalogFetcher;
    private readonly ILogger<ProductCacheInvalidatedConsumer> _logger;

    public ProductCacheInvalidatedConsumer(
        ISearchIndex index,
        ICatalogProductsApi catalogFetcher,
        ILogger<ProductCacheInvalidatedConsumer> logger)
    {
        _index = index;
        _catalogFetcher = catalogFetcher;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<ProductCacheInvalidatedEvent> context)
    {
        var msg = context.Message;
        var productKey = msg.ProductId.ToString("N");

        using var activity = SearchActivities.Source.StartActivity("search.index");
        activity?.SetTag("document.id", msg.ProductId);
        activity?.SetTag("index.name", "products");
        activity?.SetTag("index.reason", msg.Reason);
        activity?.SetTag("index.source_version", msg.NewVersion ?? 0);

        if (string.Equals(msg.Reason, "deleted", StringComparison.OrdinalIgnoreCase))
        {
            await _index.DeleteAsync(productKey, context.CancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Search index hard-deleted {ProductId}", msg.ProductId);
            return;
        }

        // Out-of-order suppression: if the index already has a document with
        // a newer or equal SourceVersion, skip the write. Spec §4 / §5.
        var existing = await _index.GetAsync(productKey, context.CancellationToken).ConfigureAwait(false);
        var incomingVersion = msg.NewVersion ?? 0;
        if (existing is not null && existing.SourceVersion >= incomingVersion)
        {
            _logger.LogInformation(
                "Skipping out-of-order ProductCacheInvalidatedEvent for {ProductId} (existing v{Existing} >= incoming v{Incoming})",
                msg.ProductId, existing.SourceVersion, incomingVersion);
            return;
        }

        CatalogProductDto fetched;
        try
        {
            fetched = await _catalogFetcher.GetProductAsync(msg.ProductId, context.CancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Catalog deleted the product between event publish and our consume.
            // The followup ProductCacheInvalidatedEvent with Reason=deleted will
            // clean up. Nothing to upsert.
            _logger.LogWarning("Catalog returned 404 for {ProductId}; deferring to deletion event", msg.ProductId);
            return;
        }

        var doc = ProductSearchDocumentProjector.From(
            id:            fetched.Id,
            name:          fetched.Name,
            description:   fetched.Description,
            unitPrice:     fetched.UnitPrice,
            isInStock:     fetched.IsInStock,
            isListed:      fetched.IsListed,
            categoryId:    fetched.CategoryId,
            categoryName:  fetched.CategoryName,
            sourceVersion: incomingVersion);

        await _index.UpsertAsync(new[] { doc }, context.CancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Search index upserted {ProductId} at v{Version}", msg.ProductId, incomingVersion);

        // Percolation (Reverse Search / Saved Searches)
        var matches = await _index.PercolateAsync(doc, context.CancellationToken).ConfigureAwait(false);
        if (matches.Count > 0)
        {
            _logger.LogInformation("Product {ProductId} matched {MatchCount} saved searches", msg.ProductId, matches.Count);
            foreach (var match in matches)
            {
                await context.Publish(new Haworks.Contracts.Search.ProductMatchedSavedSearchEvent
                {
                    SavedSearchId = match.Id,
                    UserId = match.UserId,
                    ProductId = fetched.Id,
                    ProductName = fetched.Name,
                    UnitPrice = fetched.UnitPrice,
                    MatchedAt = DateTimeOffset.UtcNow
                }, context.CancellationToken).ConfigureAwait(false);
            }
        }
    }
}
