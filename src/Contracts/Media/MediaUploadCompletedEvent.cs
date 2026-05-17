namespace Haworks.Contracts.Media;

/// <summary>
/// Fired when S3 confirms upload is complete (all bytes received).
/// Triggers the virus scan pipeline.
/// </summary>
public sealed record MediaUploadCompletedEvent : DomainEvent
{
    public required Guid MediaId { get; init; }
    public required string OwnerId { get; init; }
    public required string FileName { get; init; }
    public required string MimeType { get; init; }
    public required long Size { get; init; }
}
