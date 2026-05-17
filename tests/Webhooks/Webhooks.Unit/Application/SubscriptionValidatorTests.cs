using FluentAssertions;
using Haworks.Webhooks.Application.Subscriptions;
using Haworks.Webhooks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Haworks.Webhooks.Unit.Application;

public class SubscriptionValidatorTests
{
    private readonly CreateWebhookSubscriptionValidator _createValidator;

    public SubscriptionValidatorTests()
    {
        var options = new DbContextOptionsBuilder<WebhooksDbContext>()
            .UseInMemoryDatabase(databaseName: $"Webhooks_Validator_{Guid.NewGuid()}")
            .Options;
        var db = new WebhooksDbContext(options);
        _createValidator = new CreateWebhookSubscriptionValidator(db);
    }

    [Fact]
    public async Task Create_With_Invalid_Url_Should_Fail()
    {
        // Arrange
        var command = new CreateWebhookSubscriptionCommand(Guid.NewGuid(), "invalid-url", ["order.created"], null, true);

        // Act
        var result = await _createValidator.ValidateAsync(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(x => x.PropertyName == "Url");
    }

    [Fact]
    public async Task Create_With_Empty_Events_Should_Fail()
    {
        // Arrange
        var command = new CreateWebhookSubscriptionCommand(Guid.NewGuid(), "https://example.com", [], null, true);

        // Act
        var result = await _createValidator.ValidateAsync(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(x => x.PropertyName == "Events");
    }
}
