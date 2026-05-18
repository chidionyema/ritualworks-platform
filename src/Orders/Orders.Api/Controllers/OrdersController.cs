using Haworks.Orders.Application.Commands;
using Haworks.Orders.Application.Queries;
using Haworks.BuildingBlocks.Common;
using Haworks.BuildingBlocks.Extensions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;

namespace Haworks.Orders.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class OrdersController(IMediator mediator) : ControllerBase
{
    [HttpGet("{id:guid}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var authenticatedUserId = HttpContext.GetForwardedUserId();
        if (string.IsNullOrEmpty(authenticatedUserId)) return Unauthorized();
        var result = await mediator.Send(new GetOrderByIdQuery(id, authenticatedUserId), ct);
        return result.ToActionResult();
    }

    [HttpGet("by-user/{userId}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ListForUser(
        string userId,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 20,
        CancellationToken ct = default)
    {
        skip = Math.Max(skip, 0);
        take = Math.Clamp(take, 1, 100);
        // If the requested userId doesn't match the authenticated user, and user is not Admin, return Forbidden
        var authenticatedUserId = HttpContext.GetForwardedUserId();
        if (!string.Equals(userId, authenticatedUserId, StringComparison.Ordinal) && !User.IsInRole("Admin"))
        {
            return Forbid();
        }

        return (await mediator.Send(new ListUserOrdersQuery(userId, skip, take), ct)).ToActionResult();
    }

    [HttpGet("lookup")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
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
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create([FromBody] CreateOrderCommand command, CancellationToken ct)
    {
        // SECURITY: always take UserId from the JWT, never trust the request body.
        var userId = HttpContext.GetForwardedUserId();
        if (string.IsNullOrEmpty(userId))
            return Unauthorized();

        var safeCommand = command with { UserId = userId };
        var result = await mediator.Send(safeCommand, ct);
        return result.ToCreatedActionResult(nameof(Get), new { id = result.IsSuccess ? result.Value : Guid.Empty });
    }
}
