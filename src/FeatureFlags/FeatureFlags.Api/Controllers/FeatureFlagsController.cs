using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Haworks.FeatureFlags.Api.Application;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Haworks.FeatureFlags.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public class FeatureFlagsController : ControllerBase
{
    private readonly IMediator _mediator;

    public FeatureFlagsController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpGet("evaluate")]
    public async Task<IActionResult> Evaluate([FromQuery] string flagName, [FromQuery] string region)
    {
        // Extract userId from JWT claims to prevent impersonation via query params.
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue("sub");

        if (string.IsNullOrEmpty(userId))
            return Unauthorized();

        var result = await _mediator.Send(new EvaluateFlagQuery(flagName, userId, region));
        return result.IsSuccess ? Ok(result.Value) : BadRequest(result.Error);
    }

    [HttpPost("update")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> Update([FromBody] UpdateFlagCommand command)
    {
        var result = await _mediator.Send(command);
        return result.IsSuccess ? Ok() : BadRequest(result.Error);
    }
}
