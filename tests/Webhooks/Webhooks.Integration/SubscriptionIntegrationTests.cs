using System.Net.Http.Json;
using FluentAssertions;
using Haworks.Webhooks.Application.Subscriptions;
using Xunit;

namespace Haworks.Webhooks.Integration;

[Collection("Integration Tests")]
public class SubscriptionIntegrationTests(WebhooksWebAppFactory factory) : IClassFixture<WebhooksWebAppFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Create_Subscription_Should_Persist_To_Database()
    {
        // Arrange
        var request = new CreateWebhookSubscriptionRequest(
            PartnerId: WebhooksTestAuthHandler.TestPartnerId,
            Url: "https://partner.com/webhook",
            Events: ["order.created"],
            Secret: "super-secret"
        );

        // Act
        var response = await _client.PostAsJsonAsync("/api/webhooks/subscriptions", request);

        // Assert
        response.EnsureSuccessStatusCode();
        var subscriptionId = await response.Content.ReadFromJsonAsync<Guid>();
        subscriptionId.Should().NotBeEmpty();

        var getResponse = await _client.GetAsync($"/api/webhooks/subscriptions/{subscriptionId}");
        getResponse.EnsureSuccessStatusCode();
        var sub = await getResponse.Content.ReadFromJsonAsync<WebhookSubscriptionDto>();
        
        sub.Should().NotBeNull();
        sub!.PartnerId.Should().Be(WebhooksTestAuthHandler.TestPartnerId);
        sub.Url.Should().Be(request.Url);
        sub.Events.Should().BeEquivalentTo(request.Events);
        sub.SecretPreview.Should().Be("cret"); // last 4 chars of super-secret
    }
}
