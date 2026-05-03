namespace Haworks.Content.Api.Models;

/// <summary>
/// Request model for initializing a chunked upload session.
/// </summary>
public sealed record InitChunkSessionRequest(
    Guid EntityId,
    string FileName,
    string ContentType,
    int TotalChunks,
    long TotalSize,
    int ChunkSize = 5242880); // 5MB default
