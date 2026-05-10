using Moq;
using FluentAssertions;
using Xunit;
using Haworks.Payments.Application.Queries.Subscriptions;
using Haworks.Payments.Application.Interfaces;
using Haworks.Payments.Domain;
using Haworks.Contracts.Payments;
using FluentValidation.TestHelper;

namespace Haworks.Payments.Unit.Subscriptions;

public class GetSubscriptionStatusHandlerTests
{
    private readonly Mock<ISubscriptionManager> _managerMock = new();
    private readonly GetSubscriptionStatusQueryHandler _handler;
    private readonly GetSubscriptionStatusQueryValidator _validator = new();

    public GetSubscriptionStatusHandlerTests()
    {
        _handler = new GetSubscriptionStatusQueryHandler(_managerMock.Object);
    }

    [Fact]
    public async Task Handle_SubscriptionExists_ReturnsStatus()
    {
        // Arrange
        var userId = "user-123";
        var query = new GetSubscriptionStatusQuery(userId);
        var expectedResult = new SubscriptionStatusResult
        {
            IsActive = true,
            PlanId = "plan-abc",
            CurrentPeriodEnd = DateTime.UtcNow.AddDays(30),
            Status = SubscriptionStatus.Active,
            Provider = PaymentProvider.Stripe
        };

        _managerMock.Setup(x => x.GetStatusAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResult);

        // Act
        var result = await _handler.Handle(query, default);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.IsSubscribed.Should().BeTrue();
        result.Value.PlanId.Should().Be("plan-abc");
        result.Value.ExpiresAt.Should().Be(expectedResult.CurrentPeriodEnd);
    }

    [Fact]
    public async Task Handle_NoSubscription_ReturnsInactive()
    {
        // Arrange
        var userId = "user-123";
        var query = new GetSubscriptionStatusQuery(userId);
        var expectedResult = new SubscriptionStatusResult
        {
            IsActive = false,
            Provider = PaymentProvider.Stripe
        };

        _managerMock.Setup(x => x.GetStatusAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResult);

        // Act
        var result = await _handler.Handle(query, default);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.IsSubscribed.Should().BeFalse();
        result.Value.PlanId.Should().BeNull();
    }

    [Fact]
    public async Task Handle_ManagerThrows_Throws()
    {
        // Arrange
        var query = new GetSubscriptionStatusQuery("user-123");
        _managerMock.Setup(x => x.GetStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("DB Error"));

        // Act & Assert
        await Assert.ThrowsAsync<Exception>(() => _handler.Handle(query, default));
    }

    [Fact]
    public void Validator_EmptyUserId_HasError()
    {
        var query = new GetSubscriptionStatusQuery("");
        var result = _validator.TestValidate(query);
        result.ShouldHaveValidationErrorFor(x => x.UserId);
    }
}
