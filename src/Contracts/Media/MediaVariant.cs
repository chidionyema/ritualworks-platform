namespace Haworks.Contracts.Media;

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
