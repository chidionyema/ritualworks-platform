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

/// <summary>
/// Fired when virus scan passes and file is marked Active.
/// Triggers media processing pipeline (transcode, optimize, etc.).
/// </summary>
public sealed record MediaScanPassedEvent : DomainEvent
{
    public required Guid MediaId { get; init; }
    public required string OwnerId { get; init; }
    public required string FileName { get; init; }
    public required string MimeType { get; init; }
    public required long Size { get; init; }
}

/// <summary>
/// Fired when virus scan fails and file is quarantined/rejected.
/// </summary>
public sealed record MediaScanFailedEvent : DomainEvent
{
    public required Guid MediaId { get; init; }
    public required string OwnerId { get; init; }
    public required string FileName { get; init; }
    public required string Reason { get; init; }
}

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

/// <summary>
/// Fired when a media file is soft-deleted.
/// Consumers (Catalog, Content) should remove references.
/// </summary>
public sealed record MediaDeletedEvent : DomainEvent
{
    public required Guid MediaId { get; init; }
    public required string OwnerId { get; init; }
}

/// <summary>
/// Represents a processed variant (thumbnail, HLS playlist, WebP conversion, etc.).
/// </summary>
public sealed record MediaVariant
{
    /// <summary>Variant kind: "thumbnail-150", "thumbnail-300", "hls-720p", "webp", "audio-normalized", etc.</summary>
    public required string Kind { get; init; }
    public required string S3Key { get; init; }
    public required string MimeType { get; init; }
    public required long Size { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public int? DurationMs { get; init; }
}
