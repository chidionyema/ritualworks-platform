using Haworks.BuildingBlocks.Common;
using MediatR;

namespace Haworks.Location.Application.Queries;

public sealed record GetNearbyAddressesQuery(double Lat, double Lon, double RadiusMeters = 5000, int Limit = 20) : IRequest<Result<IReadOnlyList<NearbyAddressDto>>>;

public sealed record NearbyAddressDto(Guid Id, string Street, string Postcode, double Distance);
