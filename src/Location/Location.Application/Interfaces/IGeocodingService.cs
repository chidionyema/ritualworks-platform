namespace Haworks.Location.Application.Interfaces;

public interface IGeocodingService
{
    /// <summary>
    /// Geocodes an address or postcode into coordinates.
    /// </summary>
    Task<(double Latitude, double Longitude)?> GeocodeAsync(string address, CancellationToken ct = default);
}
