using MediatR;
using Microsoft.AspNetCore.Mvc;
using Haworks.Pricing.Application.Commands;
using Haworks.BuildingBlocks.Common;

namespace Haworks.Pricing.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class PriceController(IMediator mediator) : ControllerBase
{
    /// <summary>
    /// Computes a price quote for the given cart lines, applying any applicable promotions.
    /// </summary>
    [HttpPost("quote")]
    public async Task<IActionResult> GetQuote([FromBody] GetPriceQuoteCommand command, CancellationToken ct)
    {
        var result = await mediator.Send(command, ct);
        return result.ToActionResult();
    }
}
