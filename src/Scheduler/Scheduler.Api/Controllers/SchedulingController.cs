using Haworks.Scheduler.Application.Scheduling.Commands.ScheduleEvent;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Haworks.Scheduler.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public class SchedulingController : ControllerBase
{
    private readonly IMediator _mediator;

    public SchedulingController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpPost("schedule")]
    public async Task<IActionResult> Schedule(ScheduleEventCommand command)
    {
        var result = await _mediator.Send(command);
        return Accepted(new { result.JobId, result.AlreadyExisted });
    }
}
