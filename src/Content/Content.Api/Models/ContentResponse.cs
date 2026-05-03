namespace Haworks.Content.Api.Models;

/// <summary>
/// Response model for content.
/// </summary>
public sealed record ContentResponse(
    Guid Id,
    Guid EntityId,
    string EntityType,
    string Url,
    string ContentType,
    long FileSize);

/// <summary>
/// Response model for content upload result.
/// </summary>
public sealed record ContentUploadResponse(
    string BucketName,
    string ObjectName,
    string ContentType,
    long FileSize,
    string? VersionId = null,
    string? StorageDetails = null,
    string? Path = null);
