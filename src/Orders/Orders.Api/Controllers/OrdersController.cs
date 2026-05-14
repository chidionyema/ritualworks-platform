using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Haworks.Orders.Application.Commands;
using Haworks.Orders.Application.Queries;
using Haworks.BuildingBlocks.Common;
using Haworks.BuildingBlocks.Extensions;

namespace Haworks.Orders.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class OrdersController(IMediator mediator) : ControllerBase
{
    [HttpGet("{id:guid}")]
    [Authorize]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var result = await mediator.Send(new GetOrderByIdQuery(id), ct);
        if (!result.IsSuccess)
            return result.ToActionResult();

        var authenticatedUserId = HttpContext.GetForwardedUserId();
        if (result.Value.UserId != authenticatedUserId && !User.IsInRole("Admin"))
            return Forbid();

        return result.ToActionResult();
    }

    [HttpGet("by-user/{userId}")]
    [Authorize]
    public async Task<IActionResult> ListForUser(
        string userId,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 20,
        CancellationToken ct = default)
    {
        // If the requested userId doesn't match the authenticated user, and user is not Admin, return Forbidden
        var authenticatedUserId = HttpContext.GetForwardedUserId();
        if (userId != authenticatedUserId && !User.IsInRole("Admin"))
        {
            return Forbid();
        }

        return (await mediator.Send(new ListUserOrdersQuery(userId, skip, take), ct)).ToActionResult();
    }

    [HttpGet("lookup")]
    [AllowAnonymous]
    public async Task<IActionResult> LookupGuestOrder(
        [FromQuery] string token,
        [FromQuery] string email,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(email))
            return BadRequest("Token and email are required");

        return (await mediator.Send(new GetGuestOrderQuery(token, email), ct)).ToActionResult();
    }

    [HttpPost]
    [Authorize]
    public async Task<IActionResult> Create([FromBody] CreateOrderCommand command, CancellationToken ct)
    {
        var result = await mediator.Send(command, ct);
        // Map Result<Guid> to Result<OrderDto> or just use ToActionResult if it handles Guid
        return result.ToCreatedActionResult(nameof(Get), new { id = result.IsSuccess ? result.Value : Guid.Empty });
    }
}
