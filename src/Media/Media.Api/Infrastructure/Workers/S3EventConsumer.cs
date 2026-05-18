using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using Haworks.Media.Api.Options;
using Microsoft.Extensions.Options;

namespace Haworks.Media.Api.Infrastructure.Workers;

public sealed class S3EventConsumer(
    IServiceScopeFactory scopeFactory,
    IAmazonSQS sqs,
    IOptions<S3NotificationOptions> opts,
    ILogger<S3EventConsumer> logger) : BackgroundService
{
    private readonly S3NotificationOptions _opts = opts.Value;
    private int _consecutiveErrors;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await PollAsync(stoppingToken);
                    _consecutiveErrors = 0;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _consecutiveErrors++;
                    logger.LogError(ex, "S3 event consumer iteration failed (consecutive: {Count})", _consecutiveErrors);
                }

                // Exponential backoff on consecutive errors, capped at 60s
                var delaySec = _consecutiveErrors > 0
                    ? Math.Min(60, (int)Math.Pow(2, _consecutiveErrors))
                    : _opts.PollIntervalSeconds;
                await Task.Delay(TimeSpan.FromSeconds(delaySec), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Unhandled exception in background service {ServiceName}", nameof(S3EventConsumer));
            throw;
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        var response = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = _opts.SqsQueueUrl,
            MaxNumberOfMessages = _opts.MaxMessages,
            WaitTimeSeconds = 5,
            VisibilityTimeout = _opts.VisibilityTimeoutSeconds,
        }, ct);

        if (response.Messages.Count == 0) return;

        foreach (var message in response.Messages)
        {
            try
            {
                await HandleMessageAsync(message, ct);
                await sqs.DeleteMessageAsync(_opts.SqsQueueUrl, message.ReceiptHandle, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to process S3 event message {MessageId}", message.MessageId);
            }
        }
    }

    private async Task HandleMessageAsync(Message message, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(message.Body);

        // Collect media IDs — handle both S3→SQS and S3→SNS→SQS formats.
        // For SNS-wrapped messages, clone the Records element before the inner
        // JsonDocument is disposed to avoid use-after-free.
        var mediaIds = new List<Guid>();

        if (doc.RootElement.TryGetProperty("Records", out var directRecords))
        {
            ExtractMediaIds(directRecords, mediaIds);
        }
        else if (doc.RootElement.TryGetProperty("Message", out var snsMessage))
        {
            using var inner = JsonDocument.Parse(snsMessage.GetString()!);
            if (inner.RootElement.TryGetProperty("Records", out var snsRecords))
            {
                // Clone before inner is disposed
                var cloned = snsRecords.Clone();
                ExtractMediaIds(cloned, mediaIds);
            }
        }
        else
        {
            logger.LogWarning("Unrecognized S3 event format: {Body}",
                message.Body[..Math.Min(500, message.Body.Length)]);
            return;
        }

        foreach (var mediaId in mediaIds)
        {
            await TriggerScanAsync(mediaId, ct);
        }
    }

    private static void ExtractMediaIds(JsonElement records, List<Guid> mediaIds)
    {
        foreach (var record in records.EnumerateArray())
        {
            var eventName = record.GetProperty("eventName").GetString();
            if (eventName == null || !eventName.StartsWith("ObjectCreated:", StringComparison.Ordinal))
                continue;

            var s3Key = record.GetProperty("s3").GetProperty("object").GetProperty("key").GetString();
            if (!string.IsNullOrEmpty(s3Key) && Guid.TryParse(s3Key, out var mediaId))
                mediaIds.Add(mediaId);
        }
    }

    private async Task TriggerScanAsync(Guid mediaId, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<MediaDbContext>();

        var file = await context.MediaFiles.FirstOrDefaultAsync(f => f.Id == mediaId, ct);
        if (file == null)
        {
            logger.LogDebug("S3 event for unknown media {MediaId} — skipping", mediaId);
            return;
        }

        if (file.Status != MediaStatus.Pending)
        {
            logger.LogDebug("S3 event for already-processed media {MediaId} (status={Status}) — skipping",
                mediaId, file.Status);
            return;
        }

        // Publish MediaUploadCompletedEvent → MediaUploadCompletedConsumer runs the same
        // scan + event pipeline as the HTTP path, ensuring a single code path.
        var publisher = scope.ServiceProvider.GetRequiredService<MassTransit.IPublishEndpoint>();
        await publisher.Publish(new Haworks.Contracts.Media.MediaUploadCompletedEvent
        {
            MediaId = file.Id,
            OwnerId = file.OwnerId,
            FileName = file.FileName,
            MimeType = file.MimeType,
            Size = file.Size,
        }, ct);

        logger.LogInformation("S3 event published MediaUploadCompletedEvent for {MediaId}", mediaId);
    }
}
