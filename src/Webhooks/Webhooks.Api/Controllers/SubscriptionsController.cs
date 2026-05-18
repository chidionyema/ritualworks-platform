using Haworks.BuildingBlocks.Authentication;
using Haworks.BuildingBlocks.Common;
using Haworks.Webhooks.Application.Subscriptions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace Haworks.Webhooks.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/webhooks/subscriptions")]
[Authorize]
public sealed class SubscriptionsController(IMediator mediator) : ControllerBase
{
    private Guid GetPartnerId()
    {
        var raw = User.FindFirst("partner_id")?.Value ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(raw, out var id) ? id : Guid.Empty;
    }

    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create([FromBody] CreateWebhookSubscriptionRequest request, CancellationToken ct)
    {
        var partnerId = GetPartnerId();
        var command = new CreateWebhookSubscriptionCommand(
            partnerId,
            request.Url,
            request.Events,
            request.Secret,
            request.IsActive);

        var result = await mediator.Send(command, ct);
        return result.ToCreatedActionResult(nameof(Get), new { id = result.IsSuccess ? result.Value : Guid.Empty });
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        => (await mediator.Send(new GetWebhookSubscriptionQuery(id, GetPartnerId()), ct)).ToActionResult();

    [HttpPatch("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateWebhookSubscriptionCommand command, CancellationToken ct)
    {
        if (id != command.Id) return BadRequest();
        var secureCommand = command with { CallerPartnerId = GetPartnerId() };
        return (await mediator.Send(secureCommand, ct)).ToActionResult();
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        => (await mediator.Send(new DeleteWebhookSubscriptionCommand(id, GetPartnerId()), ct)).ToActionResult();

    [HttpPost("{id:guid}/rotate-secret")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> RotateSecret(Guid id, [FromBody] string? secret, CancellationToken ct)
        => (await mediator.Send(new RotateWebhookSubscriptionSecretCommand(id, secret, GetPartnerId()), ct)).ToActionResult();
}
