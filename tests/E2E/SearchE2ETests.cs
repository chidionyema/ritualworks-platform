using System.Text.Json;
using FluentAssertions;
using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;

namespace Haworks.Tests.E2E;

[Collection("E2E Tests")]
public class SearchE2ETests : IAsyncLifetime
{
    private readonly E2EEnvironmentFixture _fixture;
    private readonly ITestOutputHelper _output;
    private IAPIRequestContext _apiContext = null!;

    public SearchE2ETests(E2EEnvironmentFixture fixture, ITestOutputHelper output)
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
        await _apiContext.DisposeAsync();
    }

    [Fact]
    public async Task TheSearchLoop_CreateProduct_AppearsInSearch()
    {
        _output.WriteLine("--- STARTING THE SEARCH LOOP E2E ---");

        // 1. Setup Auth/CSRF
        var username = $"search_loop_{Guid.NewGuid():N}";
        var email = $"{username}@example.com";
        var password = "Password123!";
        
        var registerResponse = await _apiContext.PostAsync("/api/Authentication/register", new()
        {
            DataObject = new { username, email, password }
        });
        registerResponse.Status.Should().Be(201);

        var csrfResponse = await _apiContext.GetAsync("/api/Authentication/csrf-token");
        var csrfData = await csrfResponse.JsonAsync();
        var csrfToken = csrfData?.GetProperty("token").GetString();
        var csrfHeader = csrfData?.GetProperty("headerName").GetString();
        var headers = new Dictionary<string, string> { { csrfHeader!, csrfToken! } };

        // 2. Create Category and Product
        var categoryName = $"SearchLoopCat_{Guid.NewGuid():N}";
        var categoryResponse = await _apiContext.PostAsync("/api/Categories", new()
        {
            Headers = headers,
            DataObject = new { name = categoryName, description = "Search Loop E2E" }
        });
        var category = await categoryResponse.JsonAsync();
        var categoryId = category?.GetProperty("id").GetGuid();

        var productName = $"Quantum_Widget_{Guid.NewGuid():N}";
        var productResponse = await _apiContext.PostAsync("/api/Products", new()
        {
            Headers = headers,
            DataObject = new 
            { 
                name = productName, 
                description = "A product that definitely exists in Elasticsearch", 
                unitPrice = 1337.00m, 
                categoryId,
                initialStock = 50
            }
        });
        productResponse.Status.Should().Be(201);

        _output.WriteLine($"Product '{productName}' created. Waiting for indexing...");

        // 3. Wait for indexing (Elasticsearch is near-real-time, MassTransit outbox adds small lag)
        // We poll for up to 10 seconds.
        bool found = false;
        for (int i = 0; i < 10; i++)
        {
            await Task.Delay(1000);
            
            var searchResponse = await _apiContext.GetAsync($"/api/search?q={productName}");
            if (!searchResponse.Ok)
            {
                _output.WriteLine($"Search request failed with status {searchResponse.Status}. Content: {await searchResponse.TextAsync()}");
                continue;
            }

            var searchResult = await searchResponse.JsonAsync();
            var hits = searchResult?.GetProperty("hits").EnumerateArray();
            
            if (hits?.Any(h => h.GetProperty("name").GetString() == productName) == true)
            {
                found = true;
                _output.WriteLine($"Product found in search index after {i + 1} seconds.");
                break;
            }
        }

        found.Should().BeTrue("The created product should be discoverable via search within 10 seconds.");
    }
}
