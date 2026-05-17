using Haworks.BuildingBlocks.Common;
using Haworks.Location.Application.Queries;
using Haworks.Location.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;

namespace Haworks.Location.Application.QueryHandlers;

internal sealed class GetNearbyAddressesQueryHandler(ILocationDbContext dbContext) : IRequestHandler<GetNearbyAddressesQuery, Result<IReadOnlyList<NearbyAddressDto>>>
{
    public async Task<Result<IReadOnlyList<NearbyAddressDto>>> Handle(GetNearbyAddressesQuery request, CancellationToken ct)
    {
        if (request.Lat < -90 || request.Lat > 90)
            return Result.Failure<IReadOnlyList<NearbyAddressDto>>(Error.Validation("Address.InvalidLatitude", "Latitude must be between -90 and 90."));

        if (request.Lon < -180 || request.Lon > 180)
            return Result.Failure<IReadOnlyList<NearbyAddressDto>>(Error.Validation("Address.InvalidLongitude", "Longitude must be between -180 and 180."));

        if (request.RadiusMeters > 50000)
            return Result.Failure<IReadOnlyList<NearbyAddressDto>>(Error.Validation("Address.InvalidRadius", "RadiusMeters must not exceed 50000."));

        var limit = Math.Clamp(request.Limit, 1, 100);

        var point = new Point(request.Lon, request.Lat) { SRID = 4326 };

        var results = await dbContext.Addresses
            .Where(a => a.Coordinates.Distance(point) <= request.RadiusMeters)
            .OrderBy(a => a.Coordinates.Distance(point))
            .Take(limit)
            .Select(a => new NearbyAddressDto(
                a.Id,
                a.Street,
                a.Postcode,
                a.Coordinates.Distance(point)
            ))
            .ToListAsync(ct);

        return Result.Success<IReadOnlyList<NearbyAddressDto>>(results);
    }
}
