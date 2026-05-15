using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Haworks.Analytics.Api.Application.Commands;

namespace Haworks.Analytics.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public sealed class EventsController(IMediator mediator) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Track([FromBody] TrackEventCommand command, CancellationToken ct)
    {
        var result = await mediator.Send(command, ct);
        if (result.IsSuccess)
        {
            return Accepted();
        }
        
        return result.ToActionResult();
    }
}
