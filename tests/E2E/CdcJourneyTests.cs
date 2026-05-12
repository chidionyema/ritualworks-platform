using FluentAssertions;
using Haworks.BffWeb.Api.Controllers;
using Haworks.Catalog.Api.Models;
using System.Net.Http.Json;
using Xunit;

namespace Haworks.E2E;

/// <summary>
/// Platform-wide E2E test for the CDC pipeline.
/// Catalog Update -> CDC Relay -> Search Update -> BFF Cache Invalidation.
/// </summary>
public sealed class CdcJourneyTests : IClassFixture<E2EEnvironmentFixture>
{
    private readonly E2EEnvironmentFixture _fixture;

    public CdcJourneyTests(E2EEnvironmentFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Update_Product_via_Catalog_updates_Search_via_CDC()
    {
        // 1. Arrange: Identify a product
        var client = _fixture.CreateBffClient();
        var catalogClient = _fixture.CreateClient("catalog-svc");
        
        var products = await client.GetFromJsonAsync<SearchResponse>("/api/search?q=phone");
        products.Should().NotBeNull();
        var product = products!.Hits.First();
        var newName = $"Updated Phone {Guid.NewGuid():N}";

        // 2. Act: Update product in Catalog directly (bypassing search index)
        // In a real scenario, catalog would use its own API.
        var updateRequest = new { Name = newName, UnitPrice = product.UnitPrice };
        var response = await catalogClient.PutAsJsonAsync($"/products/{product.ProductId}", updateRequest);
        response.EnsureSuccessStatusCode();

        // 3. Assert: Wait for CDC relay to propagate change to Search
        await _fixture.RetryUntilAsync(async () =>
        {
            var searchResult = await client.GetFromJsonAsync<SearchResponse>($"/api/search?q={newName}");
            return searchResult != null && searchResult.TotalHits > 0;
        }, timeout: TimeSpan.FromSeconds(30));
        
        var updatedSearch = await client.GetFromJsonAsync<SearchResponse>($"/api/search?q={newName}");
        updatedSearch!.Hits.First().Name.Should().Be(newName);
    }
}
