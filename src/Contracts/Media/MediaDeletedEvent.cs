namespace Haworks.Contracts.Media;

/// <summary>
/// Fired when a media file is soft-deleted.
/// Consumers (Catalog, Content) should remove references.
/// </summary>
public sealed record MediaDeletedEvent : DomainEvent
{
    public required Guid MediaId { get; init; }
    public required string OwnerId { get; init; }
}
