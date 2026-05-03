namespace Haworks.Content.Api.Models;

/// <summary>
/// Response model for a chunk upload session.
/// </summary>
public sealed record ChunkSessionResponse(
    Guid SessionId,
    DateTime ExpiresAt,
    int TotalChunks);
