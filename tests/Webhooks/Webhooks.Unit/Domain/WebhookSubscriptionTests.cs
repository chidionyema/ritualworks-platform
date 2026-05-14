using FluentAssertions;
using Haworks.Webhooks.Domain;
using Xunit;

namespace Haworks.Webhooks.Unit.Domain;

public class WebhookSubscriptionTests
{
    [Fact]
    public void Create_Should_Initialize_Correctly()
    {
        // Arrange
        var partnerId = Guid.NewGuid();
        var url = "https://example.com/webhook";
        var secret = "secret123";
        var secretHash = "hashed_secret";
        var secretPreview = "t123";
        var events = new[] { "order.created" };

        // Act
        var sub = new WebhookSubscription(partnerId, url, secret, secretHash, secretPreview, events);

        // Assert
        sub.PartnerId.Should().Be(partnerId);
        sub.Url.Should().Be(url);
        sub.Secret.Should().Be(secret);
        sub.SecretHash.Should().Be(secretHash);
        sub.SecretPreview.Should().Be(secretPreview);
        sub.Events.Should().BeEquivalentTo(events);
        sub.IsActive.Should().BeTrue();
        sub.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Update_Should_Modify_Properties()
    {
        // Arrange
        var sub = new WebhookSubscription(Guid.NewGuid(), "https://old.com", "s", "sh", "p", ["e1"]);
        var newUrl = "https://new.com";
        var newEvents = new[] { "e2", "e3" };

        // Act
        sub.Update(newUrl, newEvents, false);

        // Assert
        sub.Url.Should().Be(newUrl);
        sub.Events.Should().BeEquivalentTo(newEvents);
        sub.IsActive.Should().BeFalse();
        sub.LastModifiedDate.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));
    }
}
