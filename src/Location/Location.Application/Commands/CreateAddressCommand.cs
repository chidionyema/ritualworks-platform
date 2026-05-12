using Haworks.BuildingBlocks.Messaging;
using Haworks.Contracts.Location;
using Haworks.Location.Domain.Entities;
using Haworks.Location.Infrastructure.Persistence;
using MediatR;
using NetTopologySuite.Geometries;

namespace Haworks.Location.Application.Commands;

/// <summary>
/// Command to create a new address record and publish a LocationUpdated event.
/// </summary>
public record CreateAddressCommand : IRequest<Guid>
{
    public required string Street { get; init; }
    public required string City { get; init; }
    public required string Postcode { get; init; }
    public required string Country { get; init; }
    public required double Latitude { get; init; }
    public required double Longitude { get; init; }
}

public class CreateAddressCommandHandler(
    LocationDbContext dbContext,
    IDomainEventPublisher publisher) : IRequestHandler<CreateAddressCommand, Guid>
{
    public async Task<Guid> Handle(CreateAddressCommand request, CancellationToken cancellationToken)
    {
        var address = new Address
        {
            Street = request.Street,
            City = request.City,
            Postcode = request.Postcode,
            Country = request.Country,
            Coordinates = new Point(request.Longitude, request.Latitude) { SRID = 4326 },
            Geohash = "placeholder", // TODO: Implement geohash service in Phase 5
            Metadata = "{}"
        };

        dbContext.Addresses.Add(address);
        
        // SaveChangesAsync triggers the EF Core Outbox, ensuring the 
        // business data and the event are written in a single transaction.
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
            Latitude = request.Latitude,
            Longitude = request.Longitude,
            Geohash = address.Geohash
        }, cancellationToken);

        return address.Id;
    }
}
