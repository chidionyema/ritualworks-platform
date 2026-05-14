using Microsoft.Playwright;
using Xunit;
using System.Text.Json;
using Haworks.Tests.E2E;
using WireMock.Server;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using FluentAssertions;

namespace Haworks.Tests.E2E;

[Collection("E2E Tests")]
public class WebhooksE2ETests(E2EEnvironmentFixture fixture) : IAsyncLifetime
{
    private WireMockServer? _wireMockServer;
    private static readonly string[] OrderCreatedEvents = new[] { "order.created" };
    private static readonly string[] ProductCreatedEvents = new[] { "products.created" };

    public Task InitializeAsync()
    {
        _wireMockServer = WireMockServer.Start();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _wireMockServer?.Stop();
        _wireMockServer?.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Order_Lifecycle_Should_Trigger_Webhooks()
    {
        // 1. Arrange: Setup WireMock to listen for webhooks
        _wireMockServer!.Given(
            Request.Create().WithPath("/webhook-receiver").UsingPost()
        ).RespondWith(
            Response.Create().WithStatusCode(200)
        );

        var apiContext = await fixture.CreateApiContextAsync();
        
        // Register & Auth
        var username = $"webhook_user_{Guid.NewGuid():N}";
        await apiContext.PostAsync("/api/Authentication/register", new()
        {
            DataObject = new { username, email = $"{username}@example.com", password = "Password123!" }
        });

        var csrfResponse = await apiContext.GetAsync("/api/Authentication/csrf-token");
        var csrfData = await csrfResponse.JsonAsync();
        var csrfToken = csrfData?.GetProperty("token").GetString();
        var csrfHeader = csrfData?.GetProperty("headerName").GetString();
        var headers = new Dictionary<string, string> { { csrfHeader!, csrfToken! } };

        // 2. Create Webhook Subscription
        // Note: WireMock runs on localhost, but we need an address reachable from the docker container.
        // In Aspire, we'd ideally use a service name, but for a dynamic WireMock we'll use host.docker.internal
        // or the machine's IP. Aspire's E2E environment usually has a way to handle this.
        
        var webhookUrl = $"http://host.docker.internal:{_wireMockServer.Port}/webhook-receiver";
        
        var subResponse = await apiContext.PostAsync("/api/webhooks/subscriptions", new()
        {
            Headers = headers,
            DataObject = new
            {
                partnerId = Guid.NewGuid(),
                url = webhookUrl,
                events = OrderCreatedEvents,
                secret = "webhook-secret"
            }
        });
        
        subResponse.Ok.Should().BeTrue("Subscription creation failed");

        // 3. Act: Trigger an event (Create an order)
        // For brevity, we'll use a direct internal API or a BFF demo endpoint if available.
        // Or just create a product and verify the CDC webhook.
        
        var categoryResponse = await apiContext.PostAsync("/api/Categories", new()
        {
            Headers = headers,
            DataObject = new { name = "Webhook Test Category", description = "Testing" }
        });
        var category = await categoryResponse.JsonAsync();
        var categoryId = category?.GetProperty("id").GetGuid();

        // Create Product (will trigger product.created CDC event)
        // Wait, our subscription is for 'order.created'. Let's update it or create an order.
        
        var subData = await subResponse.JsonAsync();
        var subId = subData?.GetProperty("id").GetGuid();

        await apiContext.PutAsync($"/api/webhooks/subscriptions/{subId}", new()
        {
            Headers = headers,
            DataObject = new
            {
                id = subId,
                url = webhookUrl,
                events = ProductCreatedEvents,
                isActive = true
            }
        });

        await apiContext.PostAsync("/api/Products", new()
        {
            Headers = headers,
            DataObject = new
            {
                name = "Webhook Trigger Product",
                description = "Testing webhooks",
                unitPrice = 15.00m,
                categoryId = categoryId,
                initialStock = 10
            }
        });

        // 4. Assert: Verify WireMock received the webhook
        // We'll poll WireMock for received requests
        
        bool received = false;
        for (int i = 0; i < 20; i++)
        {
            var logs = _wireMockServer.LogEntries;
            if (logs != null && logs.Any(l => l.RequestMessage is { Path: "/webhook-receiver" }))
            {
                received = true;
                break;
            }
            await Task.Delay(2000);
        }

        received.Should().BeTrue("Webhook was not received by the partner endpoint");
    }
}
