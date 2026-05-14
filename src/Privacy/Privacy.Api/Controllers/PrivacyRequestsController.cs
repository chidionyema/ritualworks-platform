using Haworks.Privacy.Application.Requests.Commands.InitiateRequest;
using Haworks.Privacy.Domain.Enums;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Haworks.Privacy.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class PrivacyRequestsController : ControllerBase
{
    private readonly IMediator _mediator;

    public PrivacyRequestsController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpPost]
    public async Task<IActionResult> Initiate(InitiatePrivacyRequestCommand command)
    {
        var id = await _mediator.Send(command);
        return Ok(new { RequestId = id });
    }
}
