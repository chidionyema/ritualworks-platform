using Haworks.Location.Application.Commands;
using Haworks.Location.Infrastructure.Persistence;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;

namespace Haworks.Location.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public class AddressesController(IMediator mediator, LocationDbContext dbContext) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<Guid>> Create(CreateAddressCommand command)
    {
        var id = await mediator.Send(command);
        return Ok(id);
    }

    /// <summary>
    /// Search for addresses within a given radius using PostGIS.
    /// Used for verification of spatial indexing.
    /// </summary>
    [HttpGet("nearby")]
    public async Task<ActionResult<IEnumerable<object>>> GetNearby(
        [FromQuery] double lat,
        [FromQuery] double lon,
        [FromQuery] double radiusMeters = 5000)
    {
        if (lat < -90 || lat > 90)
            return BadRequest("Latitude must be between -90 and 90.");

        if (lon < -180 || lon > 180)
            return BadRequest("Longitude must be between -180 and 180.");

        if (radiusMeters > 50000)
            return BadRequest("radiusMeters must not exceed 50000.");

        var point = new Point(lon, lat) { SRID = 4326 };
        
        var results = await dbContext.Addresses
            .Where(a => a.Coordinates.Distance(point) <= radiusMeters)
            .OrderBy(a => a.Coordinates.Distance(point))
            .Select(a => new
            {
                a.Id,
                a.Street,
                a.Postcode,
                Distance = a.Coordinates.Distance(point)
            })
            .ToListAsync();

        return Ok(results);
    }
}
