using Haworks.Contracts.Location;
using Haworks.Search.Application.Interfaces;
using Haworks.Search.Application.Models;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace Haworks.Search.Application.Consumers;

/// <summary>
/// Consumes <see cref="LocationUpdated"/> events and updates the Elasticsearch geospatial index.
/// </summary>
public class LocationUpdatedConsumer(
    ILocationSearchIndex locationIndex,
    ILogger<LocationUpdatedConsumer> logger) : IConsumer<LocationUpdated>
{
    public async Task Consume(ConsumeContext<LocationUpdated> context)
    {
        var msg = context.Message;
        logger.LogInformation("Processing LocationUpdated event for {LocationId}", msg.LocationId);

        var doc = new LocationSearchDocument
        {
            LocationId = msg.LocationId.ToString(),
            Location = new GeoPoint(msg.Latitude, msg.Longitude),
            Postcode = msg.Address.Postcode,
            Metadata = msg.Metadata
        };

        await locationIndex.UpsertAsync(doc, context.CancellationToken);
    }
}
