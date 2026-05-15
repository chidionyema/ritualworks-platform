using Haworks.BuildingBlocks.Common;
using Haworks.Location.Application.Commands;
using Haworks.Location.Application.Queries;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Haworks.Location.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public class AddressesController(IMediator mediator) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Create(CreateAddressCommand command)
    {
        var result = await mediator.Send(command);
        return result.ToActionResult();
    }

    /// <summary>
    /// Search for addresses within a given radius using PostGIS.
    /// Used for verification of spatial indexing.
    /// </summary>
    [HttpGet("nearby")]
    public async Task<IActionResult> GetNearby(
        [FromQuery] double lat,
        [FromQuery] double lon,
        [FromQuery] double radiusMeters = 5000)
    {
        var result = await mediator.Send(new GetNearbyAddressesQuery(lat, lon, radiusMeters));
        return result.ToActionResult();
    }
}
