using Haworks.CheckoutOrchestrator.Application.Commands;
using Haworks.CheckoutOrchestrator.Application.Queries;
using Haworks.CheckoutOrchestrator.Api.Models;
using Haworks.BuildingBlocks.Common;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Haworks.CheckoutOrchestrator.Api.Controllers;

/// <summary>
/// REST surface for the saga.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "Admin,Service")]
public sealed class CheckoutsController(IMediator mediator) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Start([FromBody] StartCheckoutRequest body, CancellationToken ct)
    {
        var result = await mediator.Send(new StartCheckoutCommand(
            body.SagaId,
            body.OrderId,
            body.UserId,
            body.CustomerEmail,
            body.TotalAmount,
            body.IdempotencyKey,
            body.Items
        ), ct);

        if (!result.IsSuccess)
            return result.ToActionResult();

        return Accepted(new { sagaId = result.Value.SagaId, orderId = result.Value.OrderId });
    }

    [HttpGet("{sagaId:guid}")]
    public async Task<IActionResult> Get(Guid sagaId, CancellationToken ct)
    {
        var result = await mediator.Send(new GetCheckoutSagaQuery(sagaId), ct);
        return result.ToActionResult();
    }

    [HttpGet("by-order/{orderId:guid}")]
    public async Task<IActionResult> GetByOrderId(Guid orderId, CancellationToken ct)
    {
        var result = await mediator.Send(new GetCheckoutSagaByOrderIdQuery(orderId), ct);
        return result.ToActionResult();
    }
}
