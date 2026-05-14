namespace Haworks.Search.Application.Models;

public record LocationSearchDocument
{
    public required string LocationId { get; init; }
    public required GeoPoint Location { get; init; }
    public string? Postcode { get; init; }
    public Dictionary<string, string> Metadata { get; init; } = new();
}

public record GeoPoint(double Lat, double Lon);
