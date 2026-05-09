using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;
using Haworks.BuildingBlocks.Resilience;
using Haworks.Content.Application.Interfaces;
using Haworks.Content.Application.Models;
using Haworks.Content.Application.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;

namespace Haworks.Content.Infrastructure.ExternalServices.Storage;

/// <summary>
/// AWS-SDK-based <see cref="IContentStorageService"/>. Speaks S3 to
/// any S3-compatible backend (Tigris in prod, LocalStack in tests,
/// AWS / R2 if you want them tomorrow). Wraps every S3 call in a
/// resilience policy so transient 503/throttle errors are absorbed
/// and the circuit opens if the backend is genuinely down.
/// </summary>
internal sealed class S3ContentStorageService : IContentStorageService
{
    private const string QuarantinePrefix = "quarantine/";
    private const string PolicyServiceName = "Storage";

    private readonly IAmazonS3 _s3;
    private readonly StorageOptions _opts;
    private readonly ILogger<S3ContentStorageService> _logger;
    private readonly IAsyncPolicy _policy;
    private readonly Protocol _presignProtocol;

    public S3ContentStorageService(
        IAmazonS3 s3,
        IOptions<StorageOptions> opts,
        IResiliencePolicyFactory policyFactory,
        ILogger<S3ContentStorageService> logger)
    {
        _s3 = s3;
        _opts = opts.Value;
        _logger = logger;
        // The AWS SDK defaults presigned URL Protocol to HTTPS regardless
        // of AmazonS3Config.UseHttp. Pin the protocol per-request to the
        // scheme of ServiceURL so LocalStack (HTTP) and Tigris/AWS (HTTPS)
        // both work without test-environment-specific code.
        _presignProtocol = _opts.ServiceUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            ? Protocol.HTTP
            : Protocol.HTTPS;
        _policy = policyFactory.CreatePolicy(new ResilienceOptions
        {
            ServiceName = PolicyServiceName,
            MaxRetryAttempts = 3,
            InitialRetryDelayMs = 200,
            MaxJitterMs = 100,
            CircuitBreakerThreshold = 5,
            CircuitBreakerDurationSeconds = 30,
            Bulkhead = BulkheadOptions.Storage,
        });
    }

    public Task<string> GetPresignedPutUrlAsync(
        string objectKey, string contentType, long expectedSizeBytes,
        TimeSpan expiry, CancellationToken ct = default)
    {
        // AWS SDK presigned URL generation is purely local — no
        // round-trip — so it doesn't go through the resilience policy.
        var req = new GetPreSignedUrlRequest
        {
            BucketName = _opts.BucketName,
            Key = objectKey,
            Verb = HttpVerb.PUT,
            ContentType = contentType,
            Expires = DateTime.UtcNow.Add(expiry),
            Protocol = _presignProtocol,
        };
        return Task.FromResult(_s3.GetPreSignedURL(req));
    }

    public async Task<MultipartInitResult> InitMultipartUploadAsync(
        string objectKey, string contentType, int partCount,
        TimeSpan presignTtl, CancellationToken ct = default)
    {
        var init = await _policy.ExecuteAsync(async (_, token) =>
            await _s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
            {
                BucketName = _opts.BucketName,
                Key = objectKey,
                ContentType = contentType,
            }, token).ConfigureAwait(false),
            new Context(),
            ct).ConfigureAwait(false);

        var expiry = DateTime.UtcNow.Add(presignTtl);
        var partUrls = Enumerable.Range(1, partCount).Select(partNumber =>
        {
            var partReq = new GetPreSignedUrlRequest
            {
                BucketName = _opts.BucketName,
                Key = objectKey,
                Verb = HttpVerb.PUT,
                Expires = expiry,
                UploadId = init.UploadId,
                PartNumber = partNumber,
                Protocol = _presignProtocol,
            };
            return new PresignedPartUrl(partNumber, _s3.GetPreSignedURL(partReq));
        }).ToArray();

        return new MultipartInitResult(init.UploadId, partUrls);
    }

    public async Task<StorageObjectInfo> CompleteMultipartUploadAsync(
        string objectKey, string uploadId,
        IReadOnlyList<UploadedPart> parts, CancellationToken ct = default)
    {
        var resp = await _policy.ExecuteAsync(async (_, token) =>
            await _s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
            {
                BucketName = _opts.BucketName,
                Key = objectKey,
                UploadId = uploadId,
                PartETags = parts
                    .OrderBy(p => p.PartNumber)
                    .Select(p => new PartETag(p.PartNumber, p.ETag))
                    .ToList(),
            }, token).ConfigureAwait(false),
            new Context(),
            ct).ConfigureAwait(false);

        var head = await HeadAsync(objectKey, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Object missing immediately after CompleteMultipartUpload.");
        return head with { ETag = resp.ETag.Trim('"') };
    }

    public Task AbortMultipartUploadAsync(string objectKey, string uploadId, CancellationToken ct = default)
        => _policy.ExecuteAsync(async (_, token) =>
            await _s3.AbortMultipartUploadAsync(new AbortMultipartUploadRequest
            {
                BucketName = _opts.BucketName,
                Key = objectKey,
                UploadId = uploadId,
            }, token).ConfigureAwait(false),
            new Context(),
            ct);

    public async Task<StorageObjectInfo?> HeadAsync(string objectKey, CancellationToken ct = default)
    {
        try
        {
            var head = await _policy.ExecuteAsync(async (_, token) =>
                await _s3.GetObjectMetadataAsync(_opts.BucketName, objectKey, token).ConfigureAwait(false),
                new Context(),
                ct).ConfigureAwait(false);

            return new StorageObjectInfo(
                BucketName: _opts.BucketName,
                ObjectKey: objectKey,
                ETag: head.ETag.Trim('"'),
                SizeBytes: head.ContentLength,
                ContentType: head.Headers.ContentType ?? "application/octet-stream");
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public Task<string> GetPresignedGetUrlAsync(
        string objectKey, TimeSpan expiry, CancellationToken ct = default)
    {
        var req = new GetPreSignedUrlRequest
        {
            BucketName = _opts.BucketName,
            Key = objectKey,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(expiry),
            Protocol = _presignProtocol,
        };
        return Task.FromResult(_s3.GetPreSignedURL(req));
    }

    public Task DeleteAsync(string objectKey, CancellationToken ct = default)
        => _policy.ExecuteAsync(async (_, token) =>
            await _s3.DeleteObjectAsync(_opts.BucketName, objectKey, token).ConfigureAwait(false),
            new Context(),
            ct);

    public async Task<string> ComputeSha256Async(string objectKey, CancellationToken ct = default)
    {
        // Stream the object server-side and hash. Avoids buffering the
        // whole file in memory. SHA-256 because S3's ETag is MD5 for
        // single-PUT and is opaque for multipart — we want a stable
        // content hash for audit + deduplication.
        var resp = await _policy.ExecuteAsync(async (_, token) =>
            await _s3.GetObjectAsync(_opts.BucketName, objectKey, token).ConfigureAwait(false),
            new Context(),
            ct).ConfigureAwait(false);

        await using var stream = resp.ResponseStream;
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public async Task QuarantineAsync(string objectKey, CancellationToken ct = default)
    {
        // Quarantine = server-side copy to quarantine/ prefix + delete
        // of the original. We never download bytes; the copy is done by
        // the S3 backend.
        var quarantineKey = QuarantinePrefix + objectKey;
        await _policy.ExecuteAsync(async (_, token) =>
            await _s3.CopyObjectAsync(new CopyObjectRequest
            {
                SourceBucket = _opts.BucketName,
                SourceKey = objectKey,
                DestinationBucket = _opts.BucketName,
                DestinationKey = quarantineKey,
            }, token).ConfigureAwait(false),
            new Context(),
            ct).ConfigureAwait(false);

        await DeleteAsync(objectKey, ct).ConfigureAwait(false);
        _logger.LogWarning("Quarantined object {Key} -> {QuarantineKey}", objectKey, quarantineKey);
    }
}
