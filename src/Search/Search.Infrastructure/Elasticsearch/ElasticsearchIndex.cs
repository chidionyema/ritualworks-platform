using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.IndexManagement;
using Elastic.Clients.Elasticsearch.Mapping;
using Elastic.Clients.Elasticsearch.QueryDsl;
using Haworks.Search.Application.Interfaces;
using Haworks.Search.Application.Models;
using Haworks.Search.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Haworks.Search.Infrastructure.Elasticsearch;

public sealed class ElasticsearchIndex : ISearchIndex
{
    private readonly ElasticsearchClient _client;
    private readonly ElasticsearchOptions _options;
    private readonly ILogger<ElasticsearchIndex> _logger;
    private const string SavedSearchIndexName = "saved_searches";
    private static readonly string[] SearchFields = ["name^3", "categoryName^2", "description"];

    public ElasticsearchIndex(
        ElasticsearchClient client,
        IOptions<ElasticsearchOptions> options,
        ILogger<ElasticsearchIndex> logger)
    {
        _client = client;
        _options = options.Value;
        _logger = logger;
    }

    public async Task EnsureSettingsAsync(CancellationToken ct = default)
    {
        // 1. Create Products Index
        var existsResponse = await _client.Indices.ExistsAsync(_options.IndexName, ct).ConfigureAwait(false);
        if (!existsResponse.Exists)
        {
            var createResponse = await _client.Indices.CreateAsync(_options.IndexName, c => c
                .Mappings(m => m
                    .Properties<ProductSearchDocument>(p => p
                        .Keyword(f => f.ProductIdKey)
                        .Text(f => f.Name)
                        .Text(f => f.Description)
                        .Keyword(f => f.CategoryId)
                        .Text(f => f.CategoryName)
                    )
                ), ct).ConfigureAwait(false);

            if (!createResponse.IsSuccess())
            {
                throw new InvalidOperationException($"Failed to create search index: {createResponse.DebugInformation}");
            }
        }

        // 2. Create Saved Searches Index (for Percolator)
        var savedExistsResponse = await _client.Indices.ExistsAsync(SavedSearchIndexName, ct).ConfigureAwait(false);
        if (!savedExistsResponse.Exists)
        {
            var props = new Properties();
            props.Add("query", new PercolatorProperty());
            props.Add("userId", new KeywordProperty());

            var createResponse = await _client.Indices.CreateAsync(SavedSearchIndexName, c => c
                .Mappings(m => m.Properties(props)), ct).ConfigureAwait(false);

            if (!createResponse.IsSuccess())
            {
                throw new InvalidOperationException($"Failed to create saved searches index: {createResponse.DebugInformation}");
            }
        }
    }

    public async Task UpsertAsync(IReadOnlyCollection<ProductSearchDocument> docs, CancellationToken ct = default)
    {
        var response = await _client.BulkAsync(b => b
            .Index(_options.IndexName)
            .IndexMany(docs, (op, doc) => op.Id(doc.ProductIdKey))
        , ct).ConfigureAwait(false);

        if (!response.IsSuccess())
        {
            _logger.LogError("Elasticsearch bulk upsert failed: {Debug}", response.DebugInformation);
            throw new InvalidOperationException("Search upsert failed");
        }
    }

    public async Task DeleteAsync(string productIdKey, CancellationToken ct = default)
    {
        var response = await _client.DeleteAsync(_options.IndexName, (Id)productIdKey, ct).ConfigureAwait(false);
        if (!response.IsSuccess() && response.ElasticsearchServerError?.Status != 404)
        {
            _logger.LogError("Elasticsearch delete failed: {Debug}", response.DebugInformation);
            throw new InvalidOperationException("Search delete failed");
        }
    }

    public async Task<ProductSearchDocument?> GetAsync(string productIdKey, CancellationToken ct = default)
    {
        var response = await _client.GetAsync<ProductSearchDocument>(productIdKey, g => g.Index(_options.IndexName), ct).ConfigureAwait(false);
        return response.Found ? response.Source : null;
    }

    public async Task<SearchPage> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var response = await _client.SearchAsync<ProductSearchDocument>(s => s
            .Index(_options.IndexName)
            .From((query.Page - 1) * query.PageSize)
            .Size(query.PageSize)
            .Query(q => q
                .Bool(b => b
                    .Must(m => 
                    {
                        if (string.IsNullOrWhiteSpace(query.Query))
                        {
                            m.MatchAll(ma => { });
                        }
                        else
                        {
                            m.MultiMatch(mm => mm
                                .Query(query.Query)
                                .Fields(SearchFields)
                            );
                        }
                    })
                    .Filter(f => 
                    {
                        if (query.CategoryFilter.HasValue)
                        {
                            f.Term(t => t.Field(p => p.CategoryId).Value(query.CategoryFilter.Value.ToString()));
                        }
                    })
                )
            )
        , ct).ConfigureAwait(false);

        sw.Stop();

        if (!response.IsSuccess())
        {
            _logger.LogError("Elasticsearch search failed: {Debug}", response.DebugInformation);
            throw new InvalidOperationException("Search query failed");
        }

        return new SearchPage
        {
            Hits = response.Documents.ToList(),
            TotalHits = (int)response.Total,
            TookMs = (int)sw.ElapsedMilliseconds
        };
    }

    public async Task RegisterSavedSearchAsync(string id, string userId, SearchQuery query, CancellationToken ct = default)
    {
        var queryDoc = new Dictionary<string, object>
        {
            ["userId"] = userId,
            ["query"] = new {
                @bool = new {
                    must = new[] {
                        new {
                            multi_match = new {
                                query = query.Query,
                                fields = new[] { "name", "categoryName", "description" }
                            }
                        }
                    }
                }
            }
        };

        var response = await _client.IndexAsync(queryDoc, i => i
            .Index(SavedSearchIndexName)
            .Id(id)
        , ct).ConfigureAwait(false);

        if (!response.IsSuccess())
        {
            throw new InvalidOperationException($"Failed to register saved search: {response.DebugInformation}");
        }
    }

    public async Task<IReadOnlyCollection<SavedSearchMatch>> PercolateAsync(ProductSearchDocument doc, CancellationToken ct = default)
    {
        var response = await _client.SearchAsync<SavedSearchMatchDoc>(s => s
            .Index(SavedSearchIndexName)
            .Query(q => q
                .Percolate(p => p
                    .Field(new Field("query"))
                    .Document(doc)
                )
            )
        , ct).ConfigureAwait(false);

        if (!response.IsSuccess())
        {
            _logger.LogError("Elasticsearch percolation failed: {Debug}", response.DebugInformation);
            return Array.Empty<SavedSearchMatch>();
        }

        return response.Hits.Select(h => new SavedSearchMatch
        {
            Id = h.Id ?? "",
            UserId = h.Source?.UserId ?? ""
        }).Where(m => !string.IsNullOrEmpty(m.Id)).ToList();
    }

    private sealed class SavedSearchMatchDoc
    {
        public string UserId { get; set; } = "";
    }
}
