namespace Haworks.Contracts.Location;

public record LocationUpdated(
    Guid EventId,
    DateTime OccurredAt,
    Guid LocationId,
    double Latitude,
    double Longitude,
    LocationAddress Address,
    Dictionary<string, string>? Metadata) : IDomainEvent;

public record LocationAddress(
    string? Postcode,
    string? City,
    string? Country);
