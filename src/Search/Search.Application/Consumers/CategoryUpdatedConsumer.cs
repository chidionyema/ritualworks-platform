using Haworks.Contracts.Catalog;
using Haworks.Search.Application.Interfaces;
using Haworks.Search.Application.Models;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace Haworks.Search.Application.Consumers;

public sealed class CategoryUpdatedConsumer : IConsumer<CategoryUpdatedEvent>
{
    private const int BatchSize = 1000;

    private readonly ISearchIndex _index;
    private readonly ILogger<CategoryUpdatedConsumer> _logger;

    public CategoryUpdatedConsumer(
        ISearchIndex index,
        ILogger<CategoryUpdatedConsumer> logger)
    {
        _index = index;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<CategoryUpdatedEvent> context)
    {
        var msg = context.Message;
        _logger.LogInformation("Re-denormalising categoryName for category {CategoryId} → {Name}", msg.CategoryId, msg.Name);

        var totalUpdated = 0;
        var page = 1;
        while (true)
        {
            var hits = await _index.SearchAsync(new SearchQuery
            {
                Query = "",
                CategoryFilter = msg.CategoryId,
                Page = page,
                PageSize = BatchSize,
            }, context.CancellationToken).ConfigureAwait(false);

            if (hits.Hits.Count == 0) break;

            var renamed = hits.Hits.Select(h => h with { CategoryName = msg.Name }).ToList();
            await _index.UpsertAsync(renamed, context.CancellationToken).ConfigureAwait(false);
            totalUpdated += renamed.Count;

            if (hits.Hits.Count < BatchSize) break;
            page++;
        }

        _logger.LogInformation("Re-denormalised categoryName for {Count} products in category {CategoryId}", totalUpdated, msg.CategoryId);
    }
}
