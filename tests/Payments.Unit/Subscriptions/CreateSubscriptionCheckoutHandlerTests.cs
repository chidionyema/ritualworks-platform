using Moq;
using FluentAssertions;
using Xunit;
using Haworks.Payments.Application.Commands.Subscriptions;
using Haworks.Payments.Application.Interfaces;
using Haworks.Contracts.Payments;
using FluentValidation.TestHelper;

namespace Haworks.Payments.Unit.Subscriptions;

public class CreateSubscriptionCheckoutHandlerTests
{
    private readonly Mock<ISubscriptionService> _serviceMock = new();
    private readonly CreateSubscriptionCheckoutCommandHandler _handler;
    private readonly CreateSubscriptionCheckoutCommandValidator _validator = new();

    public CreateSubscriptionCheckoutHandlerTests()
    {
        _handler = new CreateSubscriptionCheckoutCommandHandler(_serviceMock.Object);
    }

    [Fact]
    public async Task Handle_ValidRequest_ReturnsSessionInfo()
    {
        // Arrange
        var command = new CreateSubscriptionCheckoutCommand("user-1", "price-1", 10.0m, "/path");
        var expectedResult = new CheckoutSessionResult
        {
            SessionId = "sess-123",
            SessionUrl = "https://stripe.com/sess-123",
            Provider = PaymentProvider.Stripe
        };

        _serviceMock.Setup(x => x.CreateSubscriptionSessionAsync(It.IsAny<CreateSubscriptionSessionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResult);

        // Act
        var result = await _handler.Handle(command, default);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.SessionId.Should().Be("sess-123");
        result.Value.CheckoutUrl.Should().Be("https://stripe.com/sess-123");
        
        _serviceMock.Verify(x => x.CreateSubscriptionSessionAsync(
            It.Is<CreateSubscriptionSessionRequest>(r => 
                r.UserId == command.UserId && 
                r.PlanId == command.PriceId &&
                r.SuccessUrl.Contains("/path")), 
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_ServiceThrows_Throws()
    {
        // Arrange
        var command = new CreateSubscriptionCheckoutCommand("user-1", "price-1", 10.0m, null);
        _serviceMock.Setup(x => x.CreateSubscriptionSessionAsync(It.IsAny<CreateSubscriptionSessionRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Stripe Error"));

        // Act & Assert
        await Assert.ThrowsAsync<Exception>(() => _handler.Handle(command, default));
    }

    [Theory]
    [InlineData("", "price-1", 10.0)]
    [InlineData("user-1", "", 10.0)]
    [InlineData("user-1", "price-1", 0)]
    [InlineData("user-1", "price-1", -1.0)]
    public void Validator_InvalidRequest_HasErrors(string userId, string priceId, decimal amount)
    {
        var command = new CreateSubscriptionCheckoutCommand(userId, priceId, amount, null);
        var result = _validator.TestValidate(command);
        result.ShouldHaveAnyValidationError();
    }
}
