using System.ComponentModel.DataAnnotations;

namespace Haworks.Content.Application.DTOs
{
    public record ChunkSessionRequest(
        Guid EntityId,
        int ChunkSize,
        [Required(ErrorMessage = "FileName is required.")]
        string FileName,

        [Required(ErrorMessage = "ContentType is required.")]
        string ContentType,

        [Range(1, int.MaxValue, ErrorMessage = "TotalChunks must be greater than or equal to 1.")]
        int TotalChunks,

        [Range(1, long.MaxValue, ErrorMessage = "TotalSize must be greater than or equal to 1.")]
        long TotalSize
    );

    public record StorageInfo(long FileSize)
    {
        public string BucketName { get; set; } = string.Empty;
        public string ObjectName { get; set; } = string.Empty;
        public string ETag { get; set; } = string.Empty;
        public string StorageDetails { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
    }

    public record ChunkSessionDto(
        Guid SessionId,
        DateTime ExpiresAt,
        int TotalChunks);

    public record ContentDto(
        Guid Id,
        Guid EntityId,
        string EntityType,
        string Url,
        string ContentType,
        long FileSize);

    public record ContentUploadResult(
        string BucketName,
        string ObjectName,
        string ContentType,
        long FileSize,
        string VersionId,
        string StorageDetails,
        string Path
    );
}
