using Haworks.Scheduler.Application.Scheduling.Commands.ScheduleEvent;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Haworks.Scheduler.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v{version:apiVersion}/[controller]")]
public class SchedulingController : ControllerBase
{
    private readonly IMediator _mediator;

    public SchedulingController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpPost("schedule")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Schedule(ScheduleEventCommand command, CancellationToken ct = default)
    {
        var result = await _mediator.Send(command, ct);
        return Accepted(new { result.JobId, result.AlreadyExisted });
    }
}
