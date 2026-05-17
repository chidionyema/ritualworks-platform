using FluentAssertions;
using Haworks.Orders.Application.Queries;
using Haworks.Orders.Domain;
using Haworks.Orders.Domain.Interfaces;
using Moq;
using Xunit;

namespace Haworks.Orders.Unit.Queries;

public class OrderQueryHandlerTests
{
    private readonly Mock<IOrderRepository> _repositoryMock = new();
    private readonly GetOrderByIdQueryHandler _getByIdHandler;
    private readonly ListUserOrdersQueryHandler _listByUserHandler;

    public OrderQueryHandlerTests()
    {
        _getByIdHandler = new GetOrderByIdQueryHandler(_repositoryMock.Object);
        _listByUserHandler = new ListUserOrdersQueryHandler(_repositoryMock.Object);
    }

    [Fact]
    public async Task GetOrderById_WhenExists_ReturnsSuccess()
    {
        var orderId = Guid.NewGuid();
        var order = Order.Create("user-123", 100m, "USD", Guid.NewGuid(), "key", "email", 
            new List<(Guid, string, int, decimal)> { (Guid.NewGuid(), "P1", 1, 100m) });
        
        _repositoryMock.Setup(r => r.GetByIdAsync(orderId, It.IsAny<CancellationToken>())).ReturnsAsync(order);

        var result = await _getByIdHandler.Handle(new GetOrderByIdQuery(orderId, "user-123"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalAmount.Should().Be(100m);
    }

    [Fact]
    public async Task ListUserOrders_WithValidUser_ReturnsPagedResult()
    {
        var userId = "user-123";
        var order = Order.Create(userId, 100m, "USD", Guid.NewGuid(), "key", "email", 
            new List<(Guid, string, int, decimal)> { (Guid.NewGuid(), "P1", 1, 100m) });
        
        _repositoryMock.Setup(r => r.ListByUserAsync(userId, 0, 20, It.IsAny<CancellationToken>())).ReturnsAsync(new List<Order> { order });
        _repositoryMock.Setup(r => r.CountByUserAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var result = await _listByUserHandler.Handle(new ListUserOrdersQuery(userId, 0, 20), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(1);
        result.Value.Total.Should().Be(1);
    }

    [Fact]
    public async Task ListUserOrders_WithEmptyUserId_ReturnsFailure()
    {
        var result = await _listByUserHandler.Handle(new ListUserOrdersQuery("", 0, 20), CancellationToken.None);
        result.IsFailure.Should().BeTrue();
    }
}
