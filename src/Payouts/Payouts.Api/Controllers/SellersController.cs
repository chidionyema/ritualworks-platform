using Haworks.Payouts.Application.Sellers.Commands.GetOnboardingLink;
using Haworks.Payouts.Application.Sellers.Commands.RegisterSeller;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Haworks.Payouts.Api.Controllers;

[ApiController][Route("api/[controller]")]
[Authorize]
public class SellersController : ControllerBase
{
    private readonly IMediator _mediator;
    public SellersController(IMediator mediator) { _mediator = mediator; }
    [HttpPost] public async Task<IActionResult> Register(RegisterSellerCommand command) => Ok(new { ProfileId = await _mediator.Send(command) });
    [HttpPost("{sellerId}/onboarding-link")] public async Task<IActionResult> GetOnboardingLink(Guid sellerId, [FromQuery] string returnUrl, [FromQuery] string refreshUrl) => Ok(new { Url = await _mediator.Send(new GetOnboardingLinkCommand(sellerId, returnUrl, refreshUrl)) });
}
