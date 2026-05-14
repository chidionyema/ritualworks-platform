using FluentAssertions;
using Haworks.Webhooks.Application.Subscriptions;
using Xunit;

namespace Haworks.Webhooks.Unit.Application;

public class SubscriptionValidatorTests
{
    private readonly CreateWebhookSubscriptionValidator _createValidator = new();

    [Fact]
    public void Create_With_Invalid_Url_Should_Fail()
    {
        // Arrange
        var command = new CreateWebhookSubscriptionCommand(Guid.NewGuid(), "invalid-url", ["order.created"], null, true);

        // Act
        var result = _createValidator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(x => x.PropertyName == "Url");
    }

    [Fact]
    public void Create_With_Empty_Events_Should_Fail()
    {
        // Arrange
        var command = new CreateWebhookSubscriptionCommand(Guid.NewGuid(), "https://example.com", [], null, true);

        // Act
        var result = _createValidator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(x => x.PropertyName == "Events");
    }
}
