using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;

namespace Haworks.Media.Api.Infrastructure;

public interface IS3Service
{
    string GeneratePreSignedUrl(string key, string mimeType);
    Task<Stream> DownloadAsync(string key, CancellationToken ct);
    Task<string> InitiateMultipartUploadAsync(string key, string mimeType, CancellationToken ct);
    string GeneratePartPresignedUrl(string key, string uploadId, int partNumber);
    Task CompleteMultipartUploadAsync(string key, string uploadId, IList<PartETag> parts, CancellationToken ct);
    Task AbortMultipartUploadAsync(string key, string uploadId, CancellationToken ct);
    Task UploadAsync(string key, string mimeType, Stream content, CancellationToken ct);
    string GeneratePresignedGetUrl(string key);
    Task<string> DownloadToFileAsync(string key, string destinationPath, CancellationToken ct);
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

    public async Task<string> DownloadToFileAsync(string key, string destinationPath, CancellationToken ct)
    {
        var response = await _s3.GetObjectAsync(new GetObjectRequest
        {
            BucketName = _opts.BucketName,
            Key = key,
        }, ct);

        await using var fs = File.Create(destinationPath);
        await response.ResponseStream.CopyToAsync(fs, ct);
        return destinationPath;
    }

    public async Task<Stream> DownloadAsync(string key, CancellationToken ct)
    {
        var response = await _s3.GetObjectAsync(new GetObjectRequest
        {
            BucketName = _opts.BucketName,
            Key = key,
        }, ct);

        var ms = new MemoryStream();
        await response.ResponseStream.CopyToAsync(ms, ct);
        ms.Position = 0;
        return ms;
    }

    public string GeneratePreSignedUrl(string key, string mimeType)
    {
        if (!_opts.Enabled)
        {
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

    public async Task<string> InitiateMultipartUploadAsync(string key, string mimeType, CancellationToken ct)
    {
        if (!_opts.Enabled) return $"disabled-upload-{Guid.NewGuid()}";

        var response = await _s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
        {
            BucketName = _opts.BucketName,
            Key = key,
            ContentType = mimeType,
        }, ct);
        return response.UploadId;
    }

    public string GeneratePartPresignedUrl(string key, string uploadId, int partNumber)
    {
        if (!_opts.Enabled) return $"https://s3-disabled.local/{_opts.BucketName}/{key}?partNumber={partNumber}&uploadId={uploadId}";

        // S3 multipart part presigning requires UploadId and PartNumber as query parameters.
        // GetPreSignedUrlRequest supports this via the overload that includes upload metadata.
        var req = new GetPreSignedUrlRequest
        {
            BucketName = _opts.BucketName,
            Key = key,
            Verb = HttpVerb.PUT,
            UploadId = uploadId,
            PartNumber = partNumber,
            Expires = DateTime.UtcNow.AddMinutes(_opts.PresignedUrlExpiryMinutes),
            Protocol = _presignProtocol,
        };
        return _s3.GetPreSignedURL(req);
    }

    public async Task CompleteMultipartUploadAsync(string key, string uploadId, IList<PartETag> parts, CancellationToken ct)
    {
        if (!_opts.Enabled) return;

        await _s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = _opts.BucketName,
            Key = key,
            UploadId = uploadId,
            PartETags = parts.ToList(),
        }, ct);
    }

    public async Task AbortMultipartUploadAsync(string key, string uploadId, CancellationToken ct)
    {
        if (!_opts.Enabled) return;

        await _s3.AbortMultipartUploadAsync(new AbortMultipartUploadRequest
        {
            BucketName = _opts.BucketName,
            Key = key,
            UploadId = uploadId,
        }, ct);
    }

    public async Task UploadAsync(string key, string mimeType, Stream content, CancellationToken ct)
    {
        if (!_opts.Enabled) return;

        await _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _opts.BucketName,
            Key = key,
            ContentType = mimeType,
            InputStream = content,
        }, ct);
    }

    public string GeneratePresignedGetUrl(string key)
    {
        if (!_opts.Enabled)
            return $"https://s3-disabled.local/{_opts.BucketName}/{key}";

        return _s3.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = _opts.BucketName,
            Key = key,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.AddMinutes(_opts.PresignedUrlExpiryMinutes),
            Protocol = _presignProtocol,
        });
    }
}
