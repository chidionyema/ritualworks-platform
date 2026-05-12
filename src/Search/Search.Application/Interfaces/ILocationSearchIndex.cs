using Haworks.Search.Application.Models;

namespace Haworks.Search.Application.Interfaces;

public interface ILocationSearchIndex
{
    Task UpsertAsync(LocationSearchDocument doc, CancellationToken ct = default);
    Task DeleteAsync(string locationId, CancellationToken ct = default);
}
