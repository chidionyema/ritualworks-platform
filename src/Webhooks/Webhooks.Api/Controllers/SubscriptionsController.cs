using Haworks.BuildingBlocks.Authentication;
using Haworks.BuildingBlocks.Common;
using Haworks.Webhooks.Application.Subscriptions;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Haworks.Webhooks.Api.Controllers;

[ApiController]
[Route("api/webhooks/subscriptions")]
[Authorize]
public sealed class SubscriptionsController(IMediator mediator) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateWebhookSubscriptionRequest request, CancellationToken ct)
    {
        var command = new CreateWebhookSubscriptionCommand(
            request.PartnerId,
            request.Url,
            request.Events,
            request.Secret,
            request.IsActive);
            
        var result = await mediator.Send(command, ct);
        return result.ToCreatedActionResult(nameof(Get), new { id = result.IsSuccess ? result.Value : Guid.Empty });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        => (await mediator.Send(new GetWebhookSubscriptionQuery(id), ct)).ToActionResult();

    [HttpPatch("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateWebhookSubscriptionCommand command, CancellationToken ct)
    {
        if (id != command.Id) return BadRequest();
        return (await mediator.Send(command, ct)).ToActionResult();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        => (await mediator.Send(new DeleteWebhookSubscriptionCommand(id), ct)).ToActionResult();

    [HttpPost("{id:guid}/rotate-secret")]
    public async Task<IActionResult> RotateSecret(Guid id, [FromBody] string? secret, CancellationToken ct)
        => (await mediator.Send(new RotateWebhookSubscriptionSecretCommand(id, secret), ct)).ToActionResult();
}
