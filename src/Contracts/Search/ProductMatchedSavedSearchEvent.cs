namespace Haworks.Contracts.Search;

public sealed record ProductMatchedSavedSearchEvent : DomainEvent
{
    public required string SavedSearchId { get; init; }
    public required string UserId { get; init; }
    public required Guid ProductId { get; init; }
    public required string ProductName { get; init; }
    public required long UnitPriceCents { get; init; }
    public required DateTimeOffset MatchedAt { get; init; }
}
