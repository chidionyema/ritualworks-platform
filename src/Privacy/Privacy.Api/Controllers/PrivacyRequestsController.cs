using Haworks.Privacy.Application.Requests.Commands.InitiateRequest;
using Haworks.Privacy.Application.Requests.Queries.GetErasureStatus;
using Haworks.Privacy.Domain.Enums;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Haworks.Privacy.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
[EnableRateLimiting("api")]
public class PrivacyRequestsController : ControllerBase
{
    private readonly IMediator _mediator;

    public PrivacyRequestsController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Initiate(InitiatePrivacyRequestCommand command, CancellationToken ct = default)
    {
        var userIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value;
        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
            return Unauthorized();

        var secureCommand = command with { UserId = userId };
        var id = await _mediator.Send(secureCommand, ct);
        return Ok(new { RequestId = id });
    }

    /// <summary>
    /// Returns the current status of a privacy erasure request.
    /// Users can only query their own requests (enforced by user ID from JWT).
    /// </summary>
    [HttpGet("{requestId:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetStatus(Guid requestId, CancellationToken ct = default)
    {
        var userIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value;
        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
            return Unauthorized();

        var result = await _mediator.Send(new GetErasureStatusQuery(requestId, userId), ct);
        if (result is null)
            return NotFound();

        return Ok(result);
    }
}
