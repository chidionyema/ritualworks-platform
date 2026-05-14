using Haworks.BuildingBlocks.Persistence;
using NetTopologySuite.Geometries;

namespace Haworks.Location.Domain.Entities;

/// <summary>
/// Represents a master address record with geospatial coordinates.
/// </summary>
public class Address : AuditableEntity
{
    public Address() : base() { }

    public Address(Guid id) : base(id) { }

    public string Street { get; set; } = null!;
    public string City { get; set; } = null!;
    public string Postcode { get; set; } = null!;
    public string Country { get; set; } = null!;
    
    /// <summary>
    /// Geodetic coordinates (SRID 4326).
    /// </summary>
    public Point Coordinates { get; set; } = null!;
    
    /// <summary>
    /// 12-character precision geohash for grid-based pre-filtering.
    /// </summary>
    public string Geohash { get; set; } = null!;
    
    /// <summary>
    /// Flexible JSON metadata for region, district, or business-specific tags.
    /// </summary>
    public string? Metadata { get; set; }
}
