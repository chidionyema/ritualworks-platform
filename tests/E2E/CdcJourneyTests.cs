using System.Text.Json;
using FluentAssertions;
using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;

namespace Haworks.Tests.E2E;

/// <summary>
/// End-to-end test for the Debezium CDC pipeline:
/// Catalog update → Debezium → Kafka → search-svc (index update) + bff-web (cache invalidation).
/// </summary>
[Collection("E2E Tests")]
public class CdcJourneyTests : IAsyncLifetime
{
    private readonly E2EEnvironmentFixture _fixture;
    private readonly ITestOutputHelper _output;
    private IAPIRequestContext _apiContext = null!;

    public CdcJourneyTests(E2EEnvironmentFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    public async Task InitializeAsync()
    {
        _apiContext = await _fixture.CreateApiContextAsync();
    }

    public async Task DisposeAsync()
    {
        if (_apiContext != null) await _apiContext.DisposeAsync();
    }

    [SkippableFact]
    public async Task Update_Product_Via_Catalog_Propagates_To_Search_Via_CDC()
    {
        E2EEnvironmentFixture.SkipIfNotEnabled();
        _output.WriteLine("--- CDC JOURNEY: Create → Update → Verify Search Index ---");

        // 1. Auth setup
        var username = $"cdc_journey_{Guid.NewGuid():N}";
        var registerResponse = await _apiContext.PostAsync("/api/Authentication/register", new()
        {
            DataObject = new { username, email = $"{username}@example.com", password = "Password123!" }
        });
        registerResponse.Status.Should().Be(201);

        var csrfResponse = await _apiContext.GetAsync("/api/Authentication/csrf-token");
        var csrfData = await csrfResponse.JsonAsync();
        var csrfToken = csrfData?.GetProperty("token").GetString();
        var csrfHeader = csrfData?.GetProperty("headerName").GetString();
        var headers = new Dictionary<string, string> { { csrfHeader!, csrfToken! } };

        // 2. Create a category + product so Debezium captures the INSERT
        var categoryResponse = await _apiContext.PostAsync("/api/Categories", new()
        {
            Headers = headers,
            DataObject = new { name = $"CdcCat_{Guid.NewGuid():N}", description = "CDC Journey" }
        });
        var category = await categoryResponse.JsonAsync();
        var categoryId = category?.GetProperty("id").GetGuid();

        var originalName = $"CdcProduct_{Guid.NewGuid():N}";
        var productResponse = await _apiContext.PostAsync("/api/Products", new()
        {
            Headers = headers,
            DataObject = new
            {
                name = originalName,
                description = "Created for CDC journey test",
                unitPrice = 42.00m,
                categoryId,
                initialStock = 10
            }
        });
        productResponse.Status.Should().Be(201);
        var product = await productResponse.JsonAsync();
        var productId = product?.GetProperty("id").GetGuid();

        _output.WriteLine($"Product '{originalName}' ({productId}) created. Waiting for CDC indexing...");

        // 3. Wait for initial CDC propagation (Debezium → Kafka → search-svc)
        var indexed = await PollSearchAsync(originalName, timeoutSeconds: 30);
        indexed.Should().BeTrue(
            "Product should appear in search index via Debezium CDC within 30 seconds");

        // 4. Update the product name
        var updatedName = $"Updated_{Guid.NewGuid():N}";
        var updateResponse = await _apiContext.PutAsync($"/api/Products/{productId}", new()
        {
            Headers = headers,
            DataObject = new
            {
                name = updatedName,
                description = "Updated for CDC journey test",
                unitPrice = 42.00m,
                categoryId
            }
        });
        updateResponse.Ok.Should().BeTrue("Product update should succeed");

        _output.WriteLine($"Product renamed to '{updatedName}'. Waiting for CDC update propagation...");

        // 5. Verify the UPDATE propagated through CDC to the search index
        var updated = await PollSearchAsync(updatedName, timeoutSeconds: 30);
        updated.Should().BeTrue(
            "Updated product name should appear in search index via Debezium CDC within 30 seconds");

        // 6. Verify old name is gone
        var stale = await PollSearchAsync(originalName, timeoutSeconds: 3);
        stale.Should().BeFalse(
            "Old product name should no longer appear in search results after CDC update");
    }

    private async Task<bool> PollSearchAsync(string query, int timeoutSeconds)
    {
        for (int i = 0; i < timeoutSeconds; i++)
        {
            await Task.Delay(1000);

            var searchResponse = await _apiContext.GetAsync($"/api/search?q={query}");
            if (!searchResponse.Ok) continue;

            var result = await searchResponse.JsonAsync();
            var hits = result?.GetProperty("hits").EnumerateArray();

            if (hits?.Any(h => h.GetProperty("name").GetString()?.Contains(query) == true) == true)
            {
                _output.WriteLine($"  Found '{query}' in search index after {i + 1}s");
                return true;
            }
        }

        return false;
    }
}
