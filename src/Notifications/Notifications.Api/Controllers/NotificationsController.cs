using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MediatR;
using Haworks.Notifications.Application.Commands;
using Haworks.Notifications.Application.Queries;
using Haworks.BuildingBlocks.Common;

namespace Haworks.Notifications.Api.Controllers;

[ApiController]
[Route("api/notifications")]
[Authorize]
public sealed class NotificationsController(IMediator mediator) : ControllerBase
{
    private readonly IMediator _mediator = mediator;

    /// <summary>
    /// Accepts a notification send request. The handler enforces idempotency,
    /// preference + suppression gates, and stages a Notification in
    /// <c>Created</c> state. Downstream rendering and dispatch are driven by
    /// the <c>NotificationCreatedEvent</c> published in the same EF transaction.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Send(
        [FromBody] SendNotificationCommand command,
        CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(command, cancellationToken).ConfigureAwait(false);
        return result.ToCreatedActionResult(nameof(Get), new { id = result.IsSuccess ? result.Value : Guid.Empty });
    }

    [HttpGet("{id:guid}", Name = nameof(Get))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetNotificationQuery(id), cancellationToken).ConfigureAwait(false);
        return result.ToActionResult();
    }
}
