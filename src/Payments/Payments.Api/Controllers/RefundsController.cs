using Haworks.Payments.Application.Commands.Refunds;
using Haworks.Payments.Application.Queries.Refunds;
using Haworks.BuildingBlocks.Extensions;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Haworks.Payments.Api.Controllers;

[ApiController]
[Route("api/refunds")]
[Authorize] // Ideally restricted to Admin roles
public sealed class RefundsController(IMediator mediator) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Create(
        [FromBody] CreateRefundRequest body, 
        CancellationToken ct)
    {
        var command = new CreateRefundCommand(
            body.PaymentId,
            body.Amount,
            body.Currency,
            body.Reason,
            body.RequestedBy);

        var result = await mediator.Send(command, ct);
        return result.ToActionResult();
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetStatus(Guid id, CancellationToken ct)
    {
        var result = await mediator.Send(new GetRefundSagaStateQuery(id), ct);
        return result.ToActionResult();
    }
}

public sealed record CreateRefundRequest(
    Guid PaymentId,
    decimal Amount,
    string Currency,
    string? Reason = null,
    string? RequestedBy = null);
