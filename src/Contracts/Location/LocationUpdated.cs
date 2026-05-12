namespace Haworks.Contracts.Location;

/// <summary>
/// Published when a location's address or coordinates are updated.
/// Consumed by search-svc to update the geospatial index.
/// </summary>
public record LocationUpdated : DomainEvent
{
    public required Guid LocationId { get; init; }
    public required AddressInfo Address { get; init; }
    public required double Latitude { get; init; }
    public required double Longitude { get; init; }
    public required string Geohash { get; init; }
    public Dictionary<string, string> Metadata { get; init; } = new();
}

public record AddressInfo
{
    public required string Street { get; init; }
    public required string City { get; init; }
    public required string Postcode { get; init; }
    public required string Country { get; init; }
}
