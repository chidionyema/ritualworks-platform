using Microsoft.AspNetCore.Http;
using Haworks.Content.Application.DTOs;
using Haworks.Content.Domain.Entities;
using Haworks.Content.Domain.ValueObjects;

namespace Haworks.Content.Application.Interfaces;

public interface IContentService
{
    Task<ContentEntity> UploadFileAsync(FileUploadRequest request);
    Task<FileSignatureValidationResult> ValidateFileSignatureAsync(Stream fileStream);
    Task<VirusScanResult> ScanForVirusesAsync(Stream fileStream);
}

public record FileUploadRequest(IFormFile File, string UserId, Guid EntityId);

public interface IChunkedUploadService
{
    Task<ChunkSession> InitSessionAsync(ChunkSessionRequest request);
    Task ProcessChunkAsync(Guid sessionId, int chunkIndex, Stream chunkData);
    Task<ContentEntity?> CompleteSessionAsync(Guid sessionId, string userId);
    Task<ChunkSession> GetSessionAsync(Guid sessionId);
}

public interface IContentStorageService
{
    Task<ContentUploadResult> UploadAsync(
        Stream fileStream,
        string bucketName,
        string objectName,
        string contentType,
        IDictionary<string, string> metadata,
        CancellationToken cancellationToken = default);

    Task<string> GetPresignedUrlAsync(
        string bucketName,
        string objectName,
        TimeSpan expiry,
        bool requireAuth = true,
        CancellationToken cancellationToken = default);

    Task<Stream> DownloadAsync(
        string bucketName,
        string objectName,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        string bucketName,
        string objectName,
        CancellationToken cancellationToken = default);

    Task EnsureBucketExistsAsync(
        string bucketName,
        CancellationToken cancellationToken = default);
}

public interface IFileValidator
{
    Task<FileValidationResult> ValidateAsync(IFormFile file);
    Task<FileValidationResult> ValidateMetadataAsync(string fileName, string contentType, long totalSize);
}

public interface IFileSignatureValidator
{
    Task<FileSignatureValidationResult> ValidateAsync(Stream fileStream);
}

public interface IVirusScanner
{
    Task<VirusScanResult> ScanAsync(Stream fileStream);
}
