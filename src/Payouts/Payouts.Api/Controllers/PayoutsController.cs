using Haworks.Payouts.Application.Disbursements.Queries.GetPayoutsBySeller;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Haworks.Payouts.Api.Controllers;

[ApiController][Route("api/[controller]")]
[Authorize]
public class PayoutsController : ControllerBase
{
    private readonly IMediator _mediator;
    public PayoutsController(IMediator mediator) { _mediator = mediator; }
    [HttpGet("seller/{sellerId}")] public async Task<IActionResult> GetPayouts(Guid sellerId) => Ok(await _mediator.Send(new GetPayoutsBySellerQuery(sellerId)));
}
