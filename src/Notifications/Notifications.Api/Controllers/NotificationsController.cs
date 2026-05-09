using Microsoft.AspNetCore.Mvc;
using MediatR;
using Haworks.Notifications.Application.Commands;
using Haworks.Notifications.Application.Queries;

namespace Haworks.Notifications.Api.Controllers;

[ApiController]
[Route("api/notifications")]
public sealed class NotificationsController(IMediator mediator) : ControllerBase
{
    private readonly IMediator _mediator = mediator;  // referenced when L1.G fills bodies

    [HttpPost]
    public Task<IActionResult> Send(SendNotificationCommand command)
        => Task.FromResult<IActionResult>(StatusCode(501, "Track L1.G owns this body"));

    [HttpGet("{id:guid}")]
    public Task<IActionResult> Get(Guid id)
        => Task.FromResult<IActionResult>(StatusCode(501, "Track L1.G owns this body"));
}
