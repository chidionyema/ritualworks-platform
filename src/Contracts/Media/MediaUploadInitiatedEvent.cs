namespace Haworks.Contracts.Media;

/// <summary>
/// Fired when a media upload is initiated and presigned URLs are generated.
/// </summary>
public sealed record MediaUploadInitiatedEvent : DomainEvent
{
    public required Guid MediaId { get; init; }
    public required string OwnerId { get; init; }
    public required string FileName { get; init; }
    public required string MimeType { get; init; }
    public required long Size { get; init; }
}
