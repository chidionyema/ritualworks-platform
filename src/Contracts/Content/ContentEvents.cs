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

/// <summary>
/// Fired when a content upload fails virus/signature validation and is quarantined.
/// Consumers: Notifications (alert owner), Audit (record quarantine).
/// </summary>
public sealed record ContentQuarantinedEvent : DomainEvent
{
    public required Guid ContentId { get; init; }
    public required string EntityId { get; init; }
    public required string EntityType { get; init; }
    public required string OwnerUserId { get; init; }
    public required string Reason { get; init; }
}
