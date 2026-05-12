using Elastic.Clients.Elasticsearch;
using FluentAssertions;
using Haworks.Search.Application.Interfaces;
using Haworks.Search.Application.Models;
using Haworks.Search.Infrastructure.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using SearchQuery = Haworks.Search.Application.Models.SearchQuery;

namespace Haworks.Search.Integration;

/// <summary>
/// Black-box coverage of the Elasticsearch wrapper: settings bootstrap,
/// upsert/get/delete roundtrips, SearchAsync, and PercolateAsync.
/// </summary>
[Collection("Search Integration")]
public sealed class ElasticsearchIndexTests : IAsyncLifetime
{
    private readonly SearchWebAppFactory _factory;
    private IServiceScope _scope = null!;
    private ISearchIndex _index = null!;
    private ElasticsearchClient _client = null!;
    private ElasticsearchOptions _options = null!;

    public ElasticsearchIndexTests(SearchWebAppFactory factory)
    {
        _factory = factory;
    }

    public async Task InitializeAsync()
    {
        _scope = _factory.Services.CreateScope();
        _index = _scope.ServiceProvider.GetRequiredService<ISearchIndex>();
        _client = _scope.ServiceProvider.GetRequiredService<ElasticsearchClient>();
        _options = _scope.ServiceProvider.GetRequiredService<IOptions<ElasticsearchOptions>>().Value;

        await _index.EnsureSettingsAsync();
    }

    public Task DisposeAsync()
    {
        _scope.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task EnsureSettingsAsync_is_idempotent()
    {
        await _index.EnsureSettingsAsync();
        await _index.EnsureSettingsAsync();
        // No exception → idempotency holds.
    }

    [Fact]
    public async Task Upsert_then_Get_roundtrips_a_document()
    {
        var doc = NewDoc("Wireless Headphones", "Audio");
        await _index.UpsertAsync(new[] { doc });

        var fetched = await _index.GetAsync(doc.ProductIdKey);

        fetched.Should().NotBeNull();
        fetched!.Name.Should().Be(doc.Name);
        fetched.CategoryName.Should().Be(doc.CategoryName);
        fetched.UnitPrice.Should().Be(doc.UnitPrice);
        fetched.SourceVersion.Should().Be(doc.SourceVersion);
    }

    [Fact]
    public async Task Delete_removes_a_document()
    {
        var doc = NewDoc("Toaster", "Kitchen");
        await _index.UpsertAsync(new[] { doc });

        (await _index.GetAsync(doc.ProductIdKey)).Should().NotBeNull();

        await _index.DeleteAsync(doc.ProductIdKey);

        (await _index.GetAsync(doc.ProductIdKey)).Should().BeNull();
    }

    [Fact]
    public async Task SearchAsync_returns_seeded_doc_for_term_in_name()
    {
        var token = $"unicornium_{Guid.NewGuid():N}".Substring(0, 16);
        var doc = NewDoc($"Premium {token} pen", "Stationery");
        await _index.UpsertAsync(new[] { doc });

        // Elasticsearch indexing is near real-time, might need a refresh or a small wait in tests
        await _client.Indices.RefreshAsync(_options.IndexName);

        var page = await _index.SearchAsync(new SearchQuery { Query = token, Page = 1, PageSize = 10 });

        page.TotalHits.Should().BeGreaterThan(0);
        page.Hits.Should().Contain(h => h.ProductIdKey == doc.ProductIdKey);
    }

    [Fact]
    public async Task PercolateAsync_matches_saved_search()
    {
        var token = $"magic_{Guid.NewGuid():N}".Substring(0, 16);
        var savedSearchId = $"search_{Guid.NewGuid():N}";
        var userId = "test-user-id";
        
        await _index.RegisterSavedSearchAsync(savedSearchId, userId, new SearchQuery { Query = token });
        
        // Refresh saved_searches index
        await _client.Indices.RefreshAsync("saved_searches");

        var doc = NewDoc($"The {token} product", "Electronics");
        
        var matches = await _index.PercolateAsync(doc);

        matches.Should().ContainSingle(m => m.Id == savedSearchId && m.UserId == userId);
    }

    private static ProductSearchDocument NewDoc(string name, string categoryName)
    {
        var id = Guid.NewGuid();
        return new ProductSearchDocument
        {
            ProductIdKey = id.ToString("N"),
            ProductId = id.ToString(),
            Name = name,
            Description = "Lorem ipsum dolor sit amet.",
            CategoryId = Guid.NewGuid().ToString(),
            CategoryName = categoryName,
            UnitPrice = 29.99m,
            IsInStock = true,
            IsListed = true,
            SourceVersion = 1,
            IndexedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
    }
}
