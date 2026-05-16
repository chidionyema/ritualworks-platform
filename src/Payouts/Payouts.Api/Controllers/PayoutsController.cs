using Haworks.Payouts.Application.Disbursements.Queries.GetPayoutsBySeller;
using Haworks.BuildingBlocks.Extensions;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Haworks.Payouts.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class PayoutsController(IMediator mediator) : ControllerBase
{
    [HttpGet("seller/{sellerId}")]
    public async Task<IActionResult> GetPayouts(Guid sellerId)
    {
        var userId = HttpContext.GetForwardedUserId();
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        // Sellers can only view their own payouts
        if (!Guid.TryParse(userId, out var parsedUserId) || parsedUserId != sellerId)
            return Forbid();

        return Ok(await mediator.Send(new GetPayoutsBySellerQuery(sellerId)));
    }
}
