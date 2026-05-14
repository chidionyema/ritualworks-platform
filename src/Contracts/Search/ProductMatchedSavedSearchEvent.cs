namespace Haworks.Contracts.Search;

public sealed record ProductMatchedSavedSearchEvent
{
    public required string SavedSearchId { get; init; }
    public required string UserId { get; init; }
    public required Guid ProductId { get; init; }
    public required string ProductName { get; init; }
    public required decimal UnitPrice { get; init; }
    public required DateTimeOffset MatchedAt { get; init; }
}
