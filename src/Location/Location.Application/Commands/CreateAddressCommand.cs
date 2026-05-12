using Haworks.BuildingBlocks.Messaging;
using Haworks.Contracts.Location;
using Haworks.Location.Application.Interfaces;
using Haworks.Location.Domain.Entities;
using MediatR;
using NetTopologySuite.Geometries;

namespace Haworks.Location.Application.Commands;

/// <summary>
/// Command to create a new address record and publish a LocationUpdated event.
/// Coordinates are optional; if missing, the service will attempt to geocode the address.
/// </summary>
public record CreateAddressCommand : IRequest<Guid>
{
    public required string Street { get; init; }
    public required string City { get; init; }
    public required string Postcode { get; init; }
    public required string Country { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
}

public class CreateAddressCommandHandler(
    ILocationDbContext dbContext,
    IDomainEventPublisher publisher,
    IGeocodingService geocodingService,
    IGeohashService geohashService) : IRequestHandler<CreateAddressCommand, Guid>
{
    public async Task<Guid> Handle(CreateAddressCommand request, CancellationToken cancellationToken)
    {
        double lat = request.Latitude ?? 0;
        double lon = request.Longitude ?? 0;

        // 1. Geocode if coordinates are missing
        if (!request.Latitude.HasValue || !request.Longitude.HasValue)
        {
            var addressString = $"{request.Street}, {request.City}, {request.Postcode}, {request.Country}";
            var coords = await geocodingService.GeocodeAsync(addressString, cancellationToken);
            
            if (coords == null)
            {
                // If full address geocoding fails, try just the postcode
                coords = await geocodingService.GeocodeAsync(request.Postcode, cancellationToken);
            }

            if (coords != null)
            {
                lat = coords.Value.Latitude;
                lon = coords.Value.Longitude;
            }
            else if (!request.Latitude.HasValue)
            {
                throw new InvalidOperationException($"Could not geocode address: {addressString}");
            }
        }

        // 2. Generate Geohash (Level 12 for high precision storage)
        var geohash = geohashService.Encode(lat, lon, 12);

        var address = new Address
        {
            Street = request.Street,
            City = request.City,
            Postcode = request.Postcode,
            Country = request.Country,
            Coordinates = new Point(lon, lat) { SRID = 4326 },
            Geohash = geohash,
            Metadata = "{}"
        };

        dbContext.Addresses.Add(address);
        
        // SaveChangesAsync triggers the EF Core Outbox
        await dbContext.SaveChangesAsync(cancellationToken);

        // Publish event to RabbitMQ via the outbox.
        await publisher.PublishAsync(new LocationUpdated
        {
            LocationId = address.Id,
            Address = new AddressInfo
            {
                Street = address.Street,
                City = address.City,
                Postcode = address.Postcode,
                Country = address.Country
            },
            Latitude = lat,
            Longitude = lon,
            Geohash = address.Geohash
        }, cancellationToken);

        return address.Id;
    }
}
