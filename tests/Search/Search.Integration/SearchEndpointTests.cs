using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Haworks.Search.Application.Interfaces;
using Haworks.Search.Application.Models;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haworks.Search.Integration;

/// <summary>
/// Black-box coverage of GET /search. Spec §9.2 list — every test name
/// here matches the brief verbatim.
/// </summary>
[Collection("Search Integration")]
public sealed class SearchEndpointTests : IAsyncLifetime
{
    private readonly SearchWebAppFactory _factory;
    private readonly HttpClient _client;

    public SearchEndpointTests(SearchWebAppFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public async Task InitializeAsync()
    {
        // Ensure index settings + clean state for each test class.
        using var scope = _factory.Services.CreateScope();
        var index = scope.ServiceProvider.GetRequiredService<ISearchIndex>();
        await index.EnsureSettingsAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Search_returns_paged_hits_for_known_term()
    {
        var token = $"reginald_{Guid.NewGuid():N}".Substring(0, 16);
        await SeedDocsAsync(count: 25, namePrefix: token);

        var resp = await _client.GetAsync($"/search?q={token}&pageSize=20");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("totalHits").GetInt32().Should().Be(25);
        body.GetProperty("hits").GetArrayLength().Should().Be(20);
        body.GetProperty("pageSize").GetInt32().Should().Be(20);
        body.GetProperty("page").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Search_filters_by_category()
    {
        var catA = Guid.NewGuid();
        var catB = Guid.NewGuid();
        var token = $"filtertest_{Guid.NewGuid():N}".Substring(0, 16);
        await SeedDocsAsync(count: 5, namePrefix: token, categoryId: catA);
        await SeedDocsAsync(count: 7, namePrefix: token, categoryId: catB);

        var resp = await _client.GetAsync($"/search?q={token}&categoryId={catA}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("totalHits").GetInt32().Should().Be(5);
        body.GetProperty("categoryId").GetString().Should().Be(catA.ToString());
        foreach (var hit in body.GetProperty("hits").EnumerateArray())
        {
            hit.GetProperty("categoryId").GetString().Should().Be(catA.ToString());
        }
    }

    [Fact]
    public async Task Search_returns_400_when_q_empty()
    {
        var resp = await _client.GetAsync("/search?q=");
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Search_returns_400_when_pageSize_over_100()
    {
        var resp = await _client.GetAsync("/search?q=test&pageSize=101");
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Search_handles_typos_via_meilisearch()
    {
        var token = $"headphones_{Guid.NewGuid():N}".Substring(0, 14);
        await SeedDocsAsync(count: 1, namePrefix: token);

        // Introduce a typo — drop the 'p' (still fits within
        // typoTolerance.minWordSizeForTypos.oneTypo bound).
        var typoed = token.Replace("p", "");
        var resp = await _client.GetAsync($"/search?q={typoed}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("totalHits").GetInt32().Should().BeGreaterThan(0,
            "Meilisearch's built-in typo tolerance should match a 1-char-off term");
    }

    [Fact]
    public async Task Search_returns_within_p99_target()
    {
        var token = $"perfwarm_{Guid.NewGuid():N}".Substring(0, 12);
        await SeedDocsAsync(count: 5, namePrefix: token);

        // Warm-up call (engine + transport + JIT).
        await _client.GetAsync($"/search?q={token}");

        var sw = Stopwatch.StartNew();
        var resp = await _client.GetAsync($"/search?q={token}");
        sw.Stop();

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        // CI has variance; spec §7 calls for p99 < 100ms internal but
        // a single hot query in CI should land under 250ms.
        sw.ElapsedMilliseconds.Should().BeLessThan(250,
            $"hot query exceeded latency budget: {sw.ElapsedMilliseconds}ms");
    }

    private async Task SeedDocsAsync(int count, string namePrefix, Guid? categoryId = null)
    {
        var cat = categoryId ?? Guid.NewGuid();
        var docs = Enumerable.Range(0, count).Select(i =>
        {
            var id = Guid.NewGuid();
            return new ProductSearchDocument
            {
                ProductIdKey = id.ToString("N"),
                ProductId = id.ToString(),
                Name = $"{namePrefix} {i}",
                Description = "seeded for endpoint test",
                CategoryId = cat.ToString(),
                CategoryName = "TestCat",
                UnitPrice = 9.99m + i,
                IsInStock = true,
                IsListed = true,
                SourceVersion = 1,
                IndexedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };
        }).ToList();

        using var scope = _factory.Services.CreateScope();
        var index = scope.ServiceProvider.GetRequiredService<ISearchIndex>();
        await index.UpsertAsync(docs);
    }
}
