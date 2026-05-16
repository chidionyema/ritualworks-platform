using Haworks.BuildingBlocks.Authentication;
using Haworks.BuildingBlocks.Common;
using Haworks.Webhooks.Application.Deliveries;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace Haworks.Webhooks.Api.Controllers;

[ApiController]
[Route("api/webhooks/deliveries")]
[Authorize]
public sealed class DeliveriesController(IMediator mediator) : ControllerBase
{
    private Guid GetPartnerId()
    {
        var raw = User.FindFirst("partner_id")?.Value ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(raw, out var id) ? id : Guid.Empty;
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] Guid? subscriptionId,
        [FromQuery] string? eventType,
        [FromQuery] string? status,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken ct = default)
        => (await mediator.Send(new GetDeliveriesQuery(GetPartnerId(), subscriptionId, eventType, status, skip, take), ct)).ToActionResult();

    [HttpGet("{id:guid}/attempts")]
    public async Task<IActionResult> GetAttempts(Guid id, CancellationToken ct)
        => (await mediator.Send(new GetDeliveryAttemptsQuery(id, GetPartnerId()), ct)).ToActionResult();

    [HttpPost("{id:guid}/replay")]
    public async Task<IActionResult> Replay(Guid id, CancellationToken ct)
        => (await mediator.Send(new ReplayDeliveryCommand(id, GetPartnerId()), ct)).ToActionResult();
}
