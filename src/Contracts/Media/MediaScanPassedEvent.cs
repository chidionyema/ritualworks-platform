namespace Haworks.Contracts.Media;

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
