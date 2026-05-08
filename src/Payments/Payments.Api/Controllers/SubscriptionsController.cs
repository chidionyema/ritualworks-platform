using System.Security.Claims;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Haworks.Payments.Application.Commands.Subscriptions;
using Haworks.Payments.Application.Queries.Subscriptions;

namespace Haworks.Payments.Api.Controllers;

[ApiController]
[Route("api/subscriptions")]
[Authorize]
public sealed class SubscriptionsController(IMediator mediator) : ControllerBase
{
    [HttpGet("status")]
    public async Task<IActionResult> GetStatus(CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        var result = await mediator.Send(new GetSubscriptionStatusQuery(userId), ct);
        return result.ToActionResult();
    }

    [HttpPost("create-checkout-session")]
    public async Task<IActionResult> CreateCheckoutSession(
        [FromBody] CreateSubscriptionCheckoutRequest body, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        var command = new CreateSubscriptionCheckoutCommand(
            userId,
            body.PriceId,
            body.Amount,
            body.RedirectPath);

        var result = await mediator.Send(command, ct);
        return result.ToActionResult();
    }
}

public sealed record CreateSubscriptionCheckoutRequest(
    string PriceId,
    decimal Amount,
    string? RedirectPath);
