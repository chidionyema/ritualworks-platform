using Haworks.Privacy.Application.Requests.Commands.InitiateRequest;
using Haworks.Privacy.Domain.Enums;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

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
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value;
        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
            return Unauthorized();

        var secureCommand = command with { UserId = userId };
        var id = await _mediator.Send(secureCommand);
        return Ok(new { RequestId = id });
    }
}
