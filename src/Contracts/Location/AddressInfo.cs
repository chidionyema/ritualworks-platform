namespace Haworks.Contracts.Location;

public record AddressInfo
{
    public required string Street { get; init; }
    public required string City { get; init; }
    public required string Postcode { get; init; }
    public required string Country { get; init; }
}
