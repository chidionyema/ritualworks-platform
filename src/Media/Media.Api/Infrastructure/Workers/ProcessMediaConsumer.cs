using Haworks.Contracts.Media;
using Haworks.Media.Api.Infrastructure.Processing;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Haworks.Media.Api.Infrastructure.Workers;

/// <summary>
/// MassTransit consumer that runs the media processing pipeline (transcode, thumbnails, normalization)
/// asynchronously — never in the HTTP request path.
/// </summary>
public sealed class ProcessMediaConsumer(
    MediaProcessingOrchestrator orchestrator,
    IPublishEndpoint publisher,
    IServiceScopeFactory scopeFactory,
    ILogger<ProcessMediaConsumer> logger) : IConsumer<ProcessMediaCommand>
{
    public async Task Consume(ConsumeContext<ProcessMediaCommand> context)
    {
        var msg = context.Message;
        var ct = context.CancellationToken;

        // Idempotency: skip if the file is no longer in Quarantined state
        // (already processed, rejected, or deleted)
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MediaDbContext>();
        var file = await db.MediaFiles.AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == msg.MediaId, ct);

        if (file is null || file.Status != MediaStatus.Quarantined)
        {
            logger.LogInformation(
                "Skipping media processing for {MediaId}: status is {Status} (expected Quarantined)",
                msg.MediaId, file?.Status.ToString() ?? "not found");
            return;
        }

        logger.LogInformation("Starting async media processing for {MediaId} ({MimeType})", msg.MediaId, msg.MimeType);

        var variants = await orchestrator.ProcessAsync(msg.MediaId, msg.S3Key, msg.MimeType, ct);

        if (variants.Count > 0)
        {
            await publisher.Publish(new MediaProcessingCompletedEvent
            {
                MediaId = msg.MediaId,
                OwnerId = msg.OwnerId,
                FileName = msg.FileName,
                Variants = variants,
            }, ct);
        }

        logger.LogInformation("Media processing completed for {MediaId}: {Count} variants", msg.MediaId, variants.Count);
    }
}
