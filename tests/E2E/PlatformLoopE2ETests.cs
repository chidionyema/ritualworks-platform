using System.Text.Json;
using FluentAssertions;
using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;

namespace Haworks.Tests.E2E;

[Collection("E2E Tests")]
public class PlatformLoopE2ETests(E2EEnvironmentFixture fixture, ITestOutputHelper output) : IAsyncLifetime
{
    private IAPIRequestContext _bffContext = null!;

    public async Task InitializeAsync()
    {
        _bffContext = await fixture.CreateApiContextAsync();
    }

    public async Task DisposeAsync()
    {
        await _bffContext.DisposeAsync();
    }

    [Fact]
    public async Task SearchLoop_CatalogUpdate_SyncsToElasticsearch()
    {
        output.WriteLine("--- STARTING SEARCH LOOP E2E ---");

        var catalogEndpoint = fixture.GetServiceEndpoint("catalog-svc");
        var catalogContext = await fixture.Playwright.APIRequest.NewContextAsync(new APIRequestNewContextOptions
        {
            BaseURL = catalogEndpoint.ToString(),
            IgnoreHTTPSErrors = true
        });

        // 1. Create a uniquely identifiable product in Catalog
        var productName = $"E2E_Sync_Check_{Guid.NewGuid():N}";
        
        var categoryResponse = await catalogContext.PostAsync("/api/Categories", new()
        {
            DataObject = new { name = "E2E Sync Category", description = "For E2E tests" }
        });
        categoryResponse.Ok.Should().BeTrue($"Category creation failed: {await categoryResponse.TextAsync()}");
        var category = await categoryResponse.JsonAsync();
        var categoryId = category?.GetProperty("id").GetGuid();

        var productResponse = await catalogContext.PostAsync("/api/Products", new()
        {
            DataObject = new 
            { 
                name = productName, 
                description = "Should appear in search index", 
                unitPrice = 99.99m, 
                categoryId,
                initialStock = 100
            }
        });
        productResponse.Status.Should().Be(201);

        output.WriteLine($"Product '{productName}' created. Polling search for indexing...");

        // 2. Poll the Search API via BFF until the product appears
        bool found = false;
        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(1000);
            var searchResponse = await _bffContext.GetAsync($"/api/search?q={productName}");
            if (!searchResponse.Ok) continue;

            var results = await searchResponse.JsonAsync();
            if (results == null) continue;

            var hits = results.Value.GetProperty("hits").EnumerateArray();
            
            if (hits.Any(h => h.GetProperty("name").GetString() == productName))
            {
                found = true;
                output.WriteLine($"Success: Product found in Elasticsearch after {i+1}s");
                break;
            }
        }

        found.Should().BeTrue("Product should be indexed in Elasticsearch via MassTransit outbox relay");
    }

    [Fact]
    public async Task AuditTrail_UserRegistration_AppearsInAuditLog()
    {
        output.WriteLine("--- STARTING AUDIT TRAIL E2E ---");

        var identityEndpoint = fixture.GetServiceEndpoint("identity-svc");
        var identityContext = await fixture.Playwright.APIRequest.NewContextAsync(new APIRequestNewContextOptions
        {
            BaseURL = identityEndpoint.ToString(),
            IgnoreHTTPSErrors = true
        });

        // 1. Register a new user in Identity service
        var username = $"audit_check_{Guid.NewGuid():N}";
        var email = $"{username}@example.com";
        
        var registerResponse = await identityContext.PostAsync("/api/Authentication/register", new()
        {
            DataObject = new { username, email, password = "Password123!" }
        });
        registerResponse.Status.Should().Be(201, $"Registration failed: {await registerResponse.TextAsync()}");
        
        var authData = await registerResponse.JsonAsync();
        var userId = authData?.GetProperty("userId").GetString();

        output.WriteLine($"User '{username}' (ID: {userId}) registered. Polling audit logs...");

        // 2. Call Audit service directly to verify the event was captured
        var auditEndpoint = fixture.GetServiceEndpoint("audit-svc");
        var auditContext = await fixture.Playwright.APIRequest.NewContextAsync(new APIRequestNewContextOptions
        {
            BaseURL = auditEndpoint.ToString(),
            IgnoreHTTPSErrors = true
        });

        bool captured = false;
        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(1000);
            
            var auditResponse = await auditContext.GetAsync($"/audit/events?entityId={userId}");
            if (!auditResponse.Ok) continue;

            var auditPage = await auditResponse.JsonAsync();
            if (auditPage == null) continue;

            var items = auditPage.Value.GetProperty("items").EnumerateArray();
            
            if (items.Any(e => e.GetProperty("eventType").GetString() == "UserRegistered"))
            {
                captured = true;
                output.WriteLine($"Success: UserRegistration event captured in Audit DB after {i+1}s");
                break;
            }
        }

        captured.Should().BeTrue("User registration should trigger an integration event captured by the Audit service");
    }
}
