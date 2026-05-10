using Haworks.Orders.Application.Queries;
using Haworks.Orders.Application.DTOs;
using Haworks.Orders.Domain;
using Haworks.Orders.Domain.Interfaces;
using Haworks.BuildingBlocks.Testing;
using Haworks.Orders.UnitTests.Helpers;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit.Abstractions;
using Xunit;

namespace Haworks.Orders.UnitTests.Queries;

public class GetGuestOrderQueryHandlerTests : TestBase
{
    private readonly Mock<IOrderRepository> _orderRepositoryMock;
    private readonly GetGuestOrderQueryHandler _handler;

    public GetGuestOrderQueryHandlerTests(ITestOutputHelper output) : base(output)
    {
        _orderRepositoryMock = MockRepository.Create<IOrderRepository>();
        var loggerMock = new Mock<ILogger<GetGuestOrderQueryHandler>>();
        _handler = new GetGuestOrderQueryHandler(_orderRepositoryMock.Object, loggerMock.Object);
    }

    [Fact]
    public async Task Handle_WithValidTokenAndEmail_ReturnsOrder()
    {
        // Arrange
        var token = "guest-token";
        var email = "guest@example.com";
        var order = OrderTestHelpers.CreateOrder(email: email);
        var guestInfo = GuestOrderInfo.Create(order.Id, email, "First", "Last", "Addr", "City", "State", "10001", "US", null, token);

        _orderRepositoryMock.Setup(r => r.GetGuestByTokenAsync(token, It.IsAny<CancellationToken>()))
            .ReturnsAsync(guestInfo);
        _orderRepositoryMock.Setup(r => r.GetByIdAsync(order.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(order);

        var query = new GetGuestOrderQuery(token, email);

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(token, result.Value.GuestOrderToken);
    }

    [Fact]
    public async Task Handle_WhenEmailMismatch_ReturnsFailure()
    {
        // Arrange
        var token = "guest-token";
        var email = "guest@example.com";
        var order = OrderTestHelpers.CreateOrder(email: email);
        var guestInfo = GuestOrderInfo.Create(order.Id, email, "First", "Last", "Addr", "City", "State", "10001", "US", null, token);

        _orderRepositoryMock.Setup(r => r.GetGuestByTokenAsync(token, It.IsAny<CancellationToken>()))
            .ReturnsAsync(guestInfo);

        var query = new GetGuestOrderQuery(token, "wrong@email.com");

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        Assert.True(result.IsFailure);
    }
}
