using Haworks.Orders.Api.Controllers;
using Haworks.Orders.Application.Commands;
using Haworks.Orders.Application.Queries;
using Haworks.Orders.Application.DTOs;
using Haworks.BuildingBlocks.Common;
using Haworks.BuildingBlocks.Testing;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit.Abstractions;
using Xunit;

namespace Haworks.Orders.UnitTests.Controllers;

public class OrdersControllerTests : TestBase
{
    private readonly Mock<IMediator> _mediatorMock;
    private readonly OrdersController _controller;

    public OrdersControllerTests(ITestOutputHelper output) : base(output)
    {
        _mediatorMock = MockRepository.Create<IMediator>();
        _controller = new OrdersController(_mediatorMock.Object);
    }

    [Fact]
    public async Task Get_WithValidId_ReturnsOk()
    {
        var orderId = Guid.NewGuid();
        var orderDto = new OrderDto(orderId, "user", Guid.NewGuid(), "email", 100m, "USD", "Pending", null, null, DateTime.UtcNow, new List<OrderItemDto>());

        _mediatorMock
            .Setup(m => m.Send(It.IsAny<GetOrderByIdQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(orderDto));

        var result = await _controller.Get(orderId, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
    }
}
