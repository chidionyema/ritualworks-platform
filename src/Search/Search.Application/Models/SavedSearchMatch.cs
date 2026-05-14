namespace Haworks.Search.Application.Models;

public sealed record SavedSearchMatch
{
    public required string Id { get; init; }
    public required string UserId { get; init; }
}
