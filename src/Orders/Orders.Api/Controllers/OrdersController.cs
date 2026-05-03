using MediatR;
using Microsoft.AspNetCore.Mvc;
using Haworks.Orders.Application.Commands;
using Haworks.Orders.Application.Queries;

namespace Haworks.Orders.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class OrdersController(IMediator mediator) : ControllerBase
{
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        => (await mediator.Send(new GetOrderByIdQuery(id), ct)).ToActionResult();

    [HttpGet("by-user/{userId}")]
    public async Task<IActionResult> ListForUser(
        string userId,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 20,
        CancellationToken ct = default)
        => (await mediator.Send(new ListUserOrdersQuery(userId, skip, take), ct)).ToActionResult();

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateOrderCommand command, CancellationToken ct)
    {
        var result = await mediator.Send(command, ct);
        return result.ToCreatedActionResult(nameof(Get), new { id = result.IsSuccess ? result.Value : Guid.Empty });
    }
}
