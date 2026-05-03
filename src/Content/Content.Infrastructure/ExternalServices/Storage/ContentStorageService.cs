using Microsoft.Extensions.Logging;
using Minio;
using Minio.DataModel.Args;
using Minio.Exceptions;
using Polly;
using System.Text;
using Haworks.Content.Application.Interfaces;
using Haworks.Content.Application.DTOs;
using Haworks.BuildingBlocks.Resilience;

namespace Haworks.Content.Infrastructure.ExternalServices.Storage;

public class ContentStorageService : IContentStorageService
{
    private readonly IMinioClient _minioClient;
    private readonly ILogger<ContentStorageService> _logger;
    private readonly IAsyncPolicy _resiliencePolicy;

    public ContentStorageService(
        IMinioClient minioClient,
        ILogger<ContentStorageService> logger,
        IResiliencePolicyFactory resiliencePolicyFactory)
    {
        _minioClient = minioClient ?? throw new ArgumentNullException(nameof(minioClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Use predefined storage resilience options
        _resiliencePolicy = resiliencePolicyFactory.CreatePolicy(ResilienceOptions.Storage);
    }

    public async Task<ContentUploadResult> UploadAsync(
        Stream fileStream,
        string bucketName,
        string objectName,
        string contentType,
        IDictionary<string, string> metadata,
        CancellationToken cancellationToken = default)
    {
        ContentUploadResult? result = null;
        await _resiliencePolicy.ExecuteAsync(
            async (ct) =>
            {
                var putObjectArgs = new PutObjectArgs()
                    .WithBucket(bucketName)
                    .WithObject(objectName)
                    .WithStreamData(fileStream)
                    .WithObjectSize(fileStream.Length)
                    .WithContentType(contentType)
                    .WithHeaders(metadata);

                await _minioClient.PutObjectAsync(putObjectArgs, ct);

                result = new ContentUploadResult(
                    bucketName,
                    objectName,
                    contentType,
                    fileStream.Length,
                    VersionId: string.Empty,
                    StorageDetails: string.Empty,
                    Path: string.Empty
                );
            },
            cancellationToken
        );

        return result ?? throw new InvalidOperationException("Upload failed");
    }

    public async Task<string> GetPresignedUrlAsync(
        string bucketName,
        string objectName,
        TimeSpan expiry,
        bool requireAuth = true,
        CancellationToken cancellationToken = default)
    {
        string? url = null;
        await _resiliencePolicy.ExecuteAsync(
            async (ct) =>
            {
                var args = new PresignedGetObjectArgs()
                    .WithBucket(bucketName)
                    .WithObject(objectName)
                    .WithExpiry((int)expiry.TotalSeconds);

                url = await _minioClient.PresignedGetObjectAsync(args);
            },
            cancellationToken
        );

        return url ?? throw new InvalidOperationException("Failed to generate URL");
    }

    public async Task<Stream> DownloadAsync(string bucketName, string objectKey, CancellationToken cancellationToken = default)
    {
        try
        {
            var memoryStream = new MemoryStream();

            await _minioClient.GetObjectAsync(
                new GetObjectArgs()
                    .WithBucket(bucketName)
                    .WithObject(objectKey)
                    .WithCallbackStream(stream =>
                    {
                        stream.CopyTo(memoryStream);
                    }),
                cancellationToken);

            memoryStream.Position = 0;

            if (memoryStream.Length > 5)
            {
                byte[] buffer = new byte[5];
                memoryStream.Read(buffer, 0, 5);
                memoryStream.Position = 0;

                string prefix = Encoding.ASCII.GetString(buffer);
                if (prefix == "<?xml")
                {
                    using var reader = new StreamReader(memoryStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                    string xml = reader.ReadToEnd();
                    memoryStream.Position = 0;

                    _logger.LogError("Received XML response instead of object data: {Xml}", xml);
                    throw new InvalidOperationException($"Failed to download object {objectKey} from {bucketName}: Server returned XML");
                }
            }

            return memoryStream;
        }
        catch (Exception ex) when (ex is MinioException || ex is ObjectNotFoundException || ex is BucketNotFoundException)
        {
            _logger.LogError(ex, "Error downloading object {ObjectKey} from bucket {BucketName}", objectKey, bucketName);
            throw;
        }
    }

    public async Task DeleteAsync(
        string bucketName,
        string objectName,
        CancellationToken cancellationToken = default)
    {
        await _resiliencePolicy.ExecuteAsync(
            async (ct) =>
            {
                await _minioClient.RemoveObjectAsync(
                    new RemoveObjectArgs()
                        .WithBucket(bucketName)
                        .WithObject(objectName),
                    ct
                );
            },
            cancellationToken
        );
    }

    public async Task EnsureBucketExistsAsync(
        string bucketName,
        CancellationToken cancellationToken = default)
    {
        await _resiliencePolicy.ExecuteAsync(
            async (ct) =>
            {
                bool exists = await _minioClient.BucketExistsAsync(
                    new BucketExistsArgs().WithBucket(bucketName),
                    ct
                );

                if (!exists)
                {
                    await _minioClient.MakeBucketAsync(
                        new MakeBucketArgs().WithBucket(bucketName),
                        ct
                    );
                    await SetBucketPolicyAsync(bucketName, ct);
                }
            },
            cancellationToken
        );
    }

    private async Task SetBucketPolicyAsync(
        string bucketName,
        CancellationToken ct)
    {
        var policy = @$"{{
            ""Version"": ""2012-10-17"",
            ""Statement"": [
                {{
                    ""Effect"": ""Deny"",
                    ""Principal"": ""*"",
                    ""Action"": ""s3:*"",
                    ""Resource"": ""arn:aws:s3:::{bucketName}/*"",
                    ""Condition"": {{
                        ""Bool"": {{ ""aws:SecureTransport"": ""false"" }}
                    }}
                }}
            ]
        }}";

        await _minioClient.SetPolicyAsync(
            new SetPolicyArgs()
                .WithBucket(bucketName)
                .WithPolicy(policy),
            ct
        );
    }
}
