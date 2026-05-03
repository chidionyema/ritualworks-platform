using Haworks.Content.Application.DTOs;
using Xunit;
using Haworks.BuildingBlocks.Common;
using Haworks.Content.Domain.Entities;
using Haworks.Content.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using Haworks.BuildingBlocks.Testing;
using Haworks.Content.Application.Interfaces;
namespace Haworks.Content.Integration.Builders.Builders;

/// <summary>
/// Fluent builder for creating ChunkSessionRequest instances in tests.
/// </summary>
public class ChunkSessionRequestBuilder
{
    private Guid _entityId = Guid.NewGuid();
    private int _chunkSize = 1024 * 1024; // 1MB default
    private string _fileName = "testfile.mp4";
    private string _contentType = "video/mp4";
    private int _totalChunks = 3;
    private long _totalSize = 3 * 1024 * 1024; // 3MB default

    public ChunkSessionRequestBuilder WithEntityId(Guid entityId)
    {
        _entityId = entityId;
        return this;
    }

    public ChunkSessionRequestBuilder WithChunkSize(int chunkSize)
    {
        _chunkSize = chunkSize;
        return this;
    }

    public ChunkSessionRequestBuilder WithFileName(string fileName)
    {
        _fileName = fileName;
        return this;
    }

    public ChunkSessionRequestBuilder WithContentType(string contentType)
    {
        _contentType = contentType;
        return this;
    }

    public ChunkSessionRequestBuilder WithTotalChunks(int totalChunks)
    {
        _totalChunks = totalChunks;
        return this;
    }

    public ChunkSessionRequestBuilder WithTotalSize(long totalSize)
    {
        _totalSize = totalSize;
        return this;
    }

    /// <summary>
    /// Configures for a small file (single chunk).
    /// </summary>
    public ChunkSessionRequestBuilder AsSmallFile(string fileName = "small.txt")
    {
        _fileName = fileName;
        _contentType = "text/plain";
        _chunkSize = 1024;
        _totalChunks = 1;
        _totalSize = 1024;
        return this;
    }

    /// <summary>
    /// Configures for a large video file.
    /// </summary>
    public ChunkSessionRequestBuilder AsLargeVideo(string fileName = "large.mp4")
    {
        _fileName = fileName;
        _contentType = "video/mp4";
        _chunkSize = 10 * 1024 * 1024; // 10MB chunks
        _totalChunks = 10;
        _totalSize = 100 * 1024 * 1024; // 100MB total
        return this;
    }

    /// <summary>
    /// Creates an invalid request for failure testing.
    /// </summary>
    public ChunkSessionRequestBuilder AsInvalid()
    {
        _totalChunks = 0;
        _totalSize = 0;
        return this;
    }

    public ChunkSessionRequest Build() => new(
        EntityId: _entityId,
        ChunkSize: _chunkSize,
        FileName: _fileName,
        ContentType: _contentType,
        TotalChunks: _totalChunks,
        TotalSize: _totalSize
    );

    /// <summary>
    /// Creates a new builder with default values.
    /// </summary>
    public static ChunkSessionRequestBuilder Create() => new();
}
