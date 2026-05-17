using Haworks.Orders.Api.Controllers;
using Haworks.Orders.Application.Commands;
using Haworks.Orders.Application.Queries;
using Haworks.Orders.Application.DTOs;
using Haworks.BuildingBlocks.Common;
using Haworks.BuildingBlocks.Testing;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit.Abstractions;
using Xunit;
using System.Security.Claims;

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

    private void SetupHttpContext(string? forwardedUserId = null, string? role = null)
    {
        var claims = new List<Claim>();
        if (role is not null)
            claims.Add(new Claim(ClaimTypes.Role, role));

        var identity = new ClaimsIdentity(claims, "Test");
        var principal = new ClaimsPrincipal(identity);

        var httpContext = new DefaultHttpContext { User = principal };
        if (forwardedUserId is not null)
            httpContext.Request.Headers["X-User-Id"] = forwardedUserId;

        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };
    }

    [Fact]
    public async Task Get_WithValidId_ReturnsOk()
    {
        var orderId = Guid.NewGuid();
        var userId = "user-123";
        var orderDto = new OrderDto(orderId, userId, Guid.NewGuid(), "email", 100m, "USD", "Pending", null, null, DateTime.UtcNow, new List<OrderItemDto>());

        SetupHttpContext(forwardedUserId: userId);

        _mediatorMock
            .Setup(m => m.Send(
                It.Is<GetOrderByIdQuery>(q => q.Id == orderId),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(orderDto));

        var result = await _controller.Get(orderId, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<OrderDto>(okResult.Value);
        Assert.Equal(orderId, response.Id);
        Assert.Equal(userId, response.UserId);
        Assert.Equal(100m, response.TotalAmount);
        Assert.Equal("Pending", response.Status);
    }

    [Fact]
    public async Task Get_WithInvalidId_ReturnsNotFound()
    {
        var orderId = Guid.NewGuid();
        SetupHttpContext(forwardedUserId: "user-123");

        _mediatorMock
            .Setup(m => m.Send(It.IsAny<GetOrderByIdQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<OrderDto>(
                Error.NotFound("Order.NotFound", "Order not found")));

        var result = await _controller.Get(orderId, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(404, objectResult.StatusCode);
    }

    [Fact]
    public async Task Get_WithUnauthorizedUser_ReturnsForbid()
    {
        var orderId = Guid.NewGuid();

        // Set up a different user (not owner, not admin)
        SetupHttpContext(forwardedUserId: "different-user");

        _mediatorMock
            .Setup(m => m.Send(It.IsAny<GetOrderByIdQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<OrderDto>(Error.Orders.Forbidden));

        var result = await _controller.Get(orderId, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, objectResult.StatusCode);
    }
}
