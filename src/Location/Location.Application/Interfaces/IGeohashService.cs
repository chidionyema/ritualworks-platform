namespace Haworks.Location.Application.Interfaces;

public interface IGeohashService
{
    /// <summary>
    /// Encodes a latitude and longitude into a geohash string.
    /// </summary>
    string Encode(double latitude, double longitude, int precision = 12);
}
