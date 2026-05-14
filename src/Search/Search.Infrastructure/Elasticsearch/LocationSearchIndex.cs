using Elastic.Clients.Elasticsearch;
using Haworks.Search.Application.Interfaces;
using Haworks.Search.Application.Models;
using Microsoft.Extensions.Logging;

namespace Haworks.Search.Infrastructure.Elasticsearch;

/// <summary>
/// Concrete <see cref="ILocationSearchIndex"/> backed by Elasticsearch.
/// Handles geospatial indexing of postcodes and locations.
/// </summary>
public sealed class LocationSearchIndex : ILocationSearchIndex
{
    private readonly ElasticsearchClient _client;
    private readonly ILogger<LocationSearchIndex> _logger;
    private const string LocationsIndexName = "locations";

    public LocationSearchIndex(
        ElasticsearchClient client,
        ILogger<LocationSearchIndex> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task UpsertAsync(LocationSearchDocument doc, CancellationToken ct = default)
    {
        var response = await _client.IndexAsync(doc, i => i
            .Index(LocationsIndexName)
            .Id(doc.LocationId)
        , ct).ConfigureAwait(false);

        if (!response.IsSuccess())
        {
            _logger.LogError("Elasticsearch location upsert failed: {Debug}", response.DebugInformation);
            throw new InvalidOperationException("Location search upsert failed");
        }
    }

    public async Task DeleteAsync(string locationId, CancellationToken ct = default)
    {
        var response = await _client.DeleteAsync(LocationsIndexName, (Id)locationId, ct).ConfigureAwait(false);
        if (!response.IsSuccess() && response.ElasticsearchServerError?.Status != 404)
        {
            _logger.LogError("Elasticsearch location delete failed: {Debug}", response.DebugInformation);
            throw new InvalidOperationException("Location search delete failed");
        }
    }
}
