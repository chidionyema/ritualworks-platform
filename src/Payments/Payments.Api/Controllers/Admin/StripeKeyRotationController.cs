using Haworks.Payments.Application.Commands.Secrets;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Haworks.Payments.Api.Controllers.Admin;

/// <summary>
/// Admin-only endpoint for triggering Stripe key rotation.
/// Requires the "Admin" role claim.
/// </summary>
[ApiController]
[Route("admin")]
[Authorize(Roles = "Admin")]
public sealed class StripeKeyRotationController : ControllerBase
{
    private readonly IMediator _mediator;

    public StripeKeyRotationController(IMediator mediator)
    {
        _mediator = mediator;
    }

    /// <summary>
    /// Initiates a Stripe API key rotation with a dual-key overlap window.
    /// </summary>
    [HttpPost("rotate-stripe-key")]
    [ProducesResponseType(typeof(RotateStripeKeyResult), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> RotateStripeKey(
        [FromBody] RotateStripeKeyRequest request,
        CancellationToken ct)
    {
        var command = new RotateStripeKeyCommand
        {
            NewSecretKey = request.NewSecretKey
        };

        var result = await _mediator.Send(command, ct);
        return Accepted(result);
    }
}

public sealed record RotateStripeKeyRequest
{
    public required string NewSecretKey { get; init; }
}
