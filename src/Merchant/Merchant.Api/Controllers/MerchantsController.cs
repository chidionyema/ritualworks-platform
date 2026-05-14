using Haworks.Merchant.Application.Merchants.Commands.CreateMerchant;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Haworks.Merchant.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class MerchantsController : ControllerBase
{
    private readonly IMediator _mediator;

    public MerchantsController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpPost]
    public async Task<IActionResult> Create(CreateMerchantCommand command)
    {
        var id = await _mediator.Send(command);
        return Ok(new { MerchantId = id });
    }
}
