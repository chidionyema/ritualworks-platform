namespace Haworks.Contracts.Content;

/// <summary>
/// Fired when a content item is soft-deleted.
/// Consumers: Search (remove from index), Catalog (unlink references).
/// </summary>
public sealed record ContentDeletedEvent : DomainEvent
{
    public required Guid ContentId { get; init; }
    public required string EntityId { get; init; }
    public required string EntityType { get; init; }
    public required string OwnerUserId { get; init; }
}
