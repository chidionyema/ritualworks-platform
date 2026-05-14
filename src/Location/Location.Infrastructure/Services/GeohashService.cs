using System.Text;
using Haworks.Location.Application.Interfaces;

namespace Haworks.Location.Infrastructure.Services;

/// <summary>
/// Standard implementation of the Geohash algorithm.
/// </summary>
public sealed class GeohashService : IGeohashService
{
    private const string Base32 = "0123456789bcdefghjkmnpqrstuvwxyz";

    public string Encode(double latitude, double longitude, int precision = 12)
    {
        var geohash = new StringBuilder(precision);
        double minLat = -90, maxLat = 90;
        double minLon = -180, maxLon = 180;
        var isEven = true;
        var bit = 0;
        var ch = 0;

        while (geohash.Length < precision)
        {
            if (isEven)
            {
                var mid = (minLon + maxLon) / 2;
                if (longitude > mid)
                {
                    ch |= 1 << (4 - bit);
                    minLon = mid;
                }
                else
                {
                    maxLon = mid;
                }
            }
            else
            {
                var mid = (minLat + maxLat) / 2;
                if (latitude > mid)
                {
                    ch |= 1 << (4 - bit);
                    minLat = mid;
                }
                else
                {
                    maxLat = mid;
                }
            }

            isEven = !isEven;
            if (bit < 4)
            {
                bit++;
            }
            else
            {
                geohash.Append(Base32[ch]);
                bit = 0;
                ch = 0;
            }
        }

        return geohash.ToString();
    }
}
