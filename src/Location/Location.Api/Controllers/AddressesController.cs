using Haworks.BuildingBlocks.Common;
using Haworks.Location.Application.Commands;
using Haworks.Location.Application.Queries;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Haworks.Location.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v{version:apiVersion}/[controller]")]
public class AddressesController(IMediator mediator) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create(CreateAddressCommand command, CancellationToken ct = default)
    {
        var result = await mediator.Send(command, ct);
        return result.ToActionResult();
    }

    [HttpGet("nearby")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetNearby(
        [FromQuery] double lat,
        [FromQuery] double lon,
        [FromQuery] double radiusMeters = 5000,
        CancellationToken ct = default)
    {
        var result = await mediator.Send(new GetNearbyAddressesQuery(lat, lon, radiusMeters), ct);
        return result.ToActionResult();
    }
}
