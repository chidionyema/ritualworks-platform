namespace Haworks.Contracts.Media;

/// <summary>
/// Fired when media processing fails. The original file is still clean and serveable.
/// </summary>
public sealed record MediaProcessingFailedEvent : DomainEvent
{
    public required Guid MediaId { get; init; }
    public required string OwnerId { get; init; }
    public required string FileName { get; init; }
    public required string Reason { get; init; }
}
