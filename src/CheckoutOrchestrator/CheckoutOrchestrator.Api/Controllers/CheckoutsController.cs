using Haworks.CheckoutOrchestrator.Application.Commands;
using Haworks.CheckoutOrchestrator.Api.Models;
using Haworks.BuildingBlocks.Common;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Haworks.CheckoutOrchestrator.Infrastructure;

namespace Haworks.CheckoutOrchestrator.Api.Controllers;

/// <summary>
/// REST surface for the saga.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public sealed class CheckoutsController(
    IMediator mediator,
    CheckoutDbContext db) : ControllerBase
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
        var saga = await db.CheckoutSagas.AsNoTracking()
            .FirstOrDefaultAsync(s => s.CorrelationId == sagaId, ct);
        if (saga is null) return NotFound();

        return Ok(new
        {
            sagaId = saga.CorrelationId,
            currentState = saga.CurrentState,
            orderId = saga.OrderId,
            paymentId = saga.PaymentId,
            paymentCheckoutUrl = saga.PaymentCheckoutUrl,
            failureReason = saga.FailureReason,
            createdAt = saga.CreatedAt,
        });
    }
}
