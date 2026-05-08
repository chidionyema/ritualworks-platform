using Haworks.Search.Application.Interfaces;
using Haworks.Search.Application.Models;
using Haworks.Search.Infrastructure.Options;
using Meilisearch;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using MeiliIndex = Meilisearch.Index;
using MeiliSearchQuery = Meilisearch.SearchQuery;
using AppSearchQuery = Haworks.Search.Application.Models.SearchQuery;

namespace Haworks.Search.Infrastructure.Meilisearch;

/// <summary>
/// Concrete <see cref="ISearchIndex"/> backed by the Meilisearch .NET SDK.
/// All async writes (add/delete/settings) await the engine's task queue
/// before returning so callers — and tests — see a settled index.
/// </summary>
internal sealed class MeilisearchIndex : ISearchIndex
{
    private readonly MeilisearchClient _client;
    private readonly MeilisearchOptions _options;
    private readonly ILogger<MeilisearchIndex> _logger;

    public MeilisearchIndex(
        MeilisearchClient client,
        IOptions<MeilisearchOptions> options,
        ILogger<MeilisearchIndex> logger)
    {
        _client = client;
        _options = options.Value;
        _logger = logger;
    }

    private MeiliIndex GetIndex() => _client.Index(_options.IndexName);

    public async Task UpsertAsync(IReadOnlyCollection<ProductSearchDocument> docs, CancellationToken ct = default)
    {
        if (docs.Count == 0) return;

        var index = GetIndex();
        var taskInfo = await index.AddDocumentsAsync(docs, primaryKey: "productIdKey", cancellationToken: ct).ConfigureAwait(false);
        await _client.WaitForTaskAsync(taskInfo.TaskUid, cancellationToken: ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string productIdKey, CancellationToken ct = default)
    {
        var taskInfo = await GetIndex().DeleteOneDocumentAsync(productIdKey, cancellationToken: ct).ConfigureAwait(false);
        await _client.WaitForTaskAsync(taskInfo.TaskUid, cancellationToken: ct).ConfigureAwait(false);
    }

    public async Task<ProductSearchDocument?> GetAsync(string productIdKey, CancellationToken ct = default)
    {
        try
        {
            return await GetIndex().GetDocumentAsync<ProductSearchDocument>(productIdKey, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (MeilisearchApiError ex) when (ex.Code == "document_not_found")
        {
            return null;
        }
    }

    public async Task<SearchPage> SearchAsync(AppSearchQuery query, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        var meiliQuery = new MeiliSearchQuery
        {
            Limit = query.PageSize,
            Offset = (query.Page - 1) * query.PageSize,
        };

        // Filter precedence: explicit raw Filter (used by indexer's category
        // pagination in B5) wins over the structured CategoryFilter helper.
        if (!string.IsNullOrWhiteSpace(query.Filter))
        {
            meiliQuery.Filter = query.Filter;
        }
        else if (query.CategoryFilter is { } cat)
        {
            meiliQuery.Filter = $"categoryId = \"{cat}\"";
        }

        var result = await GetIndex()
            .SearchAsync<ProductSearchDocument>(query.Query, meiliQuery, cancellationToken: ct)
            .ConfigureAwait(false);

        sw.Stop();

        var hits = result.Hits.ToList();
        var total = result switch
        {
            // Limit/Offset queries land on SearchResult<T> where
            // EstimatedTotalHits is the count we want.
            SearchResult<ProductSearchDocument> sr => (int)sr.EstimatedTotalHits,
            _ => hits.Count,
        };

        return new SearchPage
        {
            Hits = hits,
            TotalHits = total,
            TookMs = sw.ElapsedMilliseconds,
        };
    }

    public async Task EnsureSettingsAsync(CancellationToken ct = default)
    {
        // 1. Create the index if it doesn't exist (idempotent).
        try
        {
            var createTask = await _client.CreateIndexAsync(_options.IndexName, primaryKey: "productIdKey", cancellationToken: ct).ConfigureAwait(false);
            await _client.WaitForTaskAsync(createTask.TaskUid, cancellationToken: ct).ConfigureAwait(false);
            _logger.LogInformation("Created Meilisearch index {IndexName}", _options.IndexName);
        }
        catch (MeilisearchApiError ex) when (ex.Code == "index_already_exists")
        {
            // Idempotent — fine.
        }

        // 2. Apply settings — spec §4 verbatim.
        var settings = new Settings
        {
            SearchableAttributes = new[] { "name", "categoryName", "description" },
            FilterableAttributes = new[] { "categoryId", "isListed", "isInStock" },
            SortableAttributes = new[] { "unitPrice", "indexedAt" },
            RankingRules = new[]
            {
                "words", "typo", "proximity", "attribute", "sort", "exactness",
                "indexedAt:desc",
            },
            TypoTolerance = new TypoTolerance
            {
                Enabled = true,
                MinWordSizeForTypos = new TypoTolerance.TypoSize { OneTypo = 4, TwoTypos = 8 },
            },
            StopWords = Array.Empty<string>(),
        };

        var settingsTask = await GetIndex().UpdateSettingsAsync(settings, cancellationToken: ct).ConfigureAwait(false);
        await _client.WaitForTaskAsync(settingsTask.TaskUid, cancellationToken: ct).ConfigureAwait(false);
        _logger.LogInformation("Applied Meilisearch settings for index {IndexName}", _options.IndexName);
    }
}
