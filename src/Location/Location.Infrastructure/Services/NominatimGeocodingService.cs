using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Haworks.Location.Application.Interfaces;

namespace Haworks.Location.Infrastructure.Services;

/// <summary>
/// Geocoding service using OpenStreetMap Nominatim API.
/// </summary>
public sealed class NominatimGeocodingService(HttpClient httpClient) : IGeocodingService
{
    public async Task<(double Latitude, double Longitude)?> GeocodeAsync(string address, CancellationToken ct = default)
    {
        // Nominatim requires a User-Agent, which is set in DependencyInjection.
        var url = $"search?q={Uri.EscapeDataString(address)}&format=json&limit=1";
        
        try
        {
            var results = await httpClient.GetFromJsonAsync<List<NominatimResult>>(url, ct);
            
            if (results == null || results.Count == 0)
                return null;

            var first = results[0];
            if (double.TryParse(first.Lat, out var lat) && double.TryParse(first.Lon, out var lon))
            {
                return (lat, lon);
            }
        }
        catch
        {
            // Fail gracefully for now
            return null;
        }

        return null;
    }

    private sealed class NominatimResult
    {
        [JsonPropertyName("lat")]
        public string Lat { get; set; } = null!;

        [JsonPropertyName("lon")]
        public string Lon { get; set; } = null!;
    }
}
