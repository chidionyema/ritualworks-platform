using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;

namespace Haworks.Media.Api.Infrastructure;

public interface IS3Service
{
    string GeneratePreSignedUrl(string key, string mimeType);
}

/// <summary>
/// Options for the Media S3 service.
/// Bound from the "Storage" configuration section.
/// When Enabled = false (environments without S3) a no-op URL is returned.
/// </summary>
public sealed class MediaStorageOptions
{
    public const string SectionName = "Storage";

    public bool Enabled { get; set; } = true;

    public string ServiceUrl { get; set; } = string.Empty;

    public string AccessKey { get; set; } = string.Empty;

    public string SecretKey { get; set; } = string.Empty;

    public string BucketName { get; set; } = string.Empty;

    /// <summary>AWS region or "auto" for Tigris/R2.</summary>
    public string Region { get; set; } = "us-east-1";

    /// <summary>Presigned PUT URL TTL in minutes.</summary>
    public int PresignedUrlExpiryMinutes { get; set; } = 60;
}

public class S3Service : IS3Service
{
    private readonly IAmazonS3 _s3;
    private readonly MediaStorageOptions _opts;
    private readonly Protocol _presignProtocol;

    public S3Service(IAmazonS3 s3, IOptions<MediaStorageOptions> opts)
    {
        _s3 = s3;
        _opts = opts.Value;
        // Pin protocol to the ServiceURL scheme so LocalStack (HTTP) and AWS/Tigris (HTTPS)
        // both produce presigned URLs on the correct scheme without environment-specific code.
        _presignProtocol = string.IsNullOrEmpty(_opts.ServiceUrl)
            || _opts.ServiceUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? Protocol.HTTPS
            : Protocol.HTTP;
    }

    public string GeneratePreSignedUrl(string key, string mimeType)
    {
        if (!_opts.Enabled)
        {
            // Dev / CI environments without an S3 backend — return a placeholder
            // so the rest of the flow (deduplication, metadata persistence) still works.
            return $"https://s3-disabled.local/{_opts.BucketName}/{key}";
        }

        var req = new GetPreSignedUrlRequest
        {
            BucketName = _opts.BucketName,
            Key = key,
            Verb = HttpVerb.PUT,
            ContentType = mimeType,
            Expires = DateTime.UtcNow.AddMinutes(_opts.PresignedUrlExpiryMinutes),
            Protocol = _presignProtocol,
        };

        return _s3.GetPreSignedURL(req);
    }
}
