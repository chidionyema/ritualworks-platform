namespace Haworks.Contracts.Media;

/// <summary>
/// Fired when media processing (transcode/thumbnails/normalization) completes successfully.
/// Consumed by Notifications service to notify the user.
/// </summary>
public sealed record MediaProcessingCompletedEvent : DomainEvent
{
    public required Guid MediaId { get; init; }
    public required string OwnerId { get; init; }
    public required string FileName { get; init; }
    public required IReadOnlyList<MediaVariant> Variants { get; init; }
}
