using FluentAssertions;
using Haworks.Search.Application.Interfaces;
using Haworks.Search.Application.Models;
using Haworks.Search.Infrastructure.Options;
using Meilisearch;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using SearchQuery = Haworks.Search.Application.Models.SearchQuery;

namespace Haworks.Search.Integration;

/// <summary>
/// Black-box coverage of the Meilisearch wrapper: settings bootstrap,
/// upsert/get/delete roundtrips, and the SearchAsync method (B6 depends
/// on it actually working — without this test it's easy to ship a stub).
/// </summary>
public sealed class MeilisearchIndexTests : IClassFixture<SearchWebAppFactory>, IAsyncLifetime
{
    private readonly SearchWebAppFactory _factory;
    private IServiceScope _scope = null!;
    private ISearchIndex _index = null!;
    private MeilisearchClient _client = null!;
    private MeilisearchOptions _options = null!;

    public MeilisearchIndexTests(SearchWebAppFactory factory)
    {
        _factory = factory;
    }

    public async Task InitializeAsync()
    {
        // Touch Services to trigger host build (Program.cs runs
        // EnsureSettingsAsync on first build, but tests want a clean index
        // for each test class — IndexName is randomised per fixture).
        _scope = _factory.Services.CreateScope();
        _index = _scope.ServiceProvider.GetRequiredService<ISearchIndex>();
        _client = _scope.ServiceProvider.GetRequiredService<MeilisearchClient>();
        _options = _scope.ServiceProvider.GetRequiredService<IOptions<MeilisearchOptions>>().Value;

        // Make sure settings are applied (Program.cs already did this on
        // first request but tests may run before it). EnsureSettings is idempotent.
        await _index.EnsureSettingsAsync();
    }

    public Task DisposeAsync()
    {
        _scope.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task EnsureSettingsAsync_creates_index_with_expected_settings()
    {
        var settings = await _client.Index(_options.IndexName).GetSettingsAsync();

        settings.SearchableAttributes.Should().BeEquivalentTo(new[] { "name", "categoryName", "description" },
            o => o.WithStrictOrdering());
        settings.FilterableAttributes.Should().BeEquivalentTo(new[] { "categoryId", "isListed", "isInStock" });
        settings.SortableAttributes.Should().BeEquivalentTo(new[] { "unitPrice", "indexedAt" });
        settings.RankingRules.Should().BeEquivalentTo(
            new[] { "words", "typo", "proximity", "attribute", "sort", "exactness", "indexedAt:desc" },
            o => o.WithStrictOrdering());
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
        // Unique term per run so this test doesn't see leftover docs from
        // earlier upsert tests in the same fixture.
        var token = $"unicornium_{Guid.NewGuid():N}".Substring(0, 16);
        var doc = NewDoc($"Premium {token} pen", "Stationery");
        await _index.UpsertAsync(new[] { doc });

        var page = await _index.SearchAsync(new SearchQuery { Query = token, PageSize = 10 });

        page.TotalHits.Should().BeGreaterThan(0);
        page.Hits.Should().Contain(h => h.ProductIdKey == doc.ProductIdKey);
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
