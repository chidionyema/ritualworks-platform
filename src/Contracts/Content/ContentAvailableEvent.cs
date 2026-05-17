namespace Haworks.Contracts.Content;

/// <summary>
/// Fired when a content upload passes validation and is marked Available.
/// Consumers: Search (index), Notifications (notify owner), Catalog (link).
/// </summary>
public sealed record ContentAvailableEvent : DomainEvent
{
    public required Guid ContentId { get; init; }
    public required string EntityId { get; init; }
    public required string EntityType { get; init; }
    public required string Slug { get; init; }
    public required string OwnerUserId { get; init; }
}
