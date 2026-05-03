using System.ComponentModel.DataAnnotations;

namespace Haworks.Content.Infrastructure.Options;

/// <summary>
/// Configuration options for MinIO object storage.
/// </summary>
public sealed class MinioOptions
{
    public const string SectionName = "MinIO";

    [Required]
    public string Endpoint { get; set; } = string.Empty;

    [Required]
    public string AccessKey { get; set; } = string.Empty;

    [Required]
    public string SecretKey { get; set; } = string.Empty;

    [Required]
    public string BucketName { get; set; } = string.Empty;

    public bool Secure { get; set; } = true;
}
