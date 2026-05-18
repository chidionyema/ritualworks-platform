using Haworks.Contracts.Media;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Haworks.Media.Api.Infrastructure.Workers;

/// <summary>
/// Consumes <see cref="MediaUploadCompletedEvent"/> from the S3EventConsumer path.
/// Runs the same scan + event pipeline as the HTTP path but without requiring
/// an authenticated HTTP user context (the OwnerId comes from the event payload).
/// </summary>
public sealed class MediaUploadCompletedConsumer(
    MediaDbContext context,
    IVirusScanner virusScanner,
    IS3Service s3,
    IPublishEndpoint publisher,
    ILogger<MediaUploadCompletedConsumer> logger) : IConsumer<MediaUploadCompletedEvent>
{
    public async Task Consume(ConsumeContext<MediaUploadCompletedEvent> ctx)
    {
        var msg = ctx.Message;
        var ct = ctx.CancellationToken;

        var file = await context.MediaFiles.FirstOrDefaultAsync(f => f.Id == msg.MediaId, ct);
        if (file == null)
        {
            logger.LogDebug("MediaUploadCompletedEvent for unknown media {MediaId} — skipping", msg.MediaId);
            return;
        }

        if (file.Status != Domain.MediaStatus.Pending)
        {
            logger.LogDebug("Media {MediaId} already processed (status={Status}) — skipping", msg.MediaId, file.Status);
            return;
        }

        var tempPath = Path.Combine(Path.GetTempPath(), $"media-scan-{Guid.NewGuid()}");
        try
        {
            await s3.DownloadToFileAsync(file.Id.ToString(), tempPath, ct);

            // Hash verification
            await using (var hashStream = File.OpenRead(tempPath))
            {
                var actualHash = Convert.ToHexStringLower(
                    await System.Security.Cryptography.SHA256.HashDataAsync(hashStream, ct));
                if (!string.Equals(actualHash, file.Hash, StringComparison.OrdinalIgnoreCase))
                {
                    await using var tx = await context.Database.BeginTransactionAsync(ct);
                    file.MarkAsQuarantined();
                    file.MarkAsRejected();
                    // MassTransit EF Outbox commits automatically

                    await publisher.Publish(new MediaScanFailedEvent
                    {
                        MediaId = file.Id,
                        OwnerId = file.OwnerId,
                        FileName = file.FileName,
                        Reason = "Server-side hash does not match declared hash.",
                    }, ct);

                    await tx.CommitAsync(ct);
                    logger.LogWarning("Hash mismatch for {MediaId} via S3 event path", msg.MediaId);
                    return;
                }
            }

            // Virus scan — use file path directly to avoid double temp file for large files
            var isClean = await virusScanner.ScanFileAsync(tempPath, ct);

            await using var tx2 = await context.Database.BeginTransactionAsync(ct);
            try
            {
                file.MarkAsQuarantined();

                if (isClean)
                {
                    file.MarkAsActive();
                    // MassTransit EF Outbox commits automatically

                    await publisher.Publish(new MediaScanPassedEvent
                    {
                        MediaId = file.Id,
                        OwnerId = file.OwnerId,
                        FileName = file.FileName,
                        MimeType = file.MimeType,
                        Size = file.Size,
                    }, ct);

                    await ctx.Send(new Uri("queue:process-media-command"), new ProcessMediaCommand
                    {
                        MediaId = file.Id,
                        OwnerId = file.OwnerId,
                        FileName = file.FileName,
                        MimeType = file.MimeType,
                        S3Key = file.Id.ToString(),
                    });
                }
                else
                {
                    file.MarkAsRejected();
                    // MassTransit EF Outbox commits automatically

                    await publisher.Publish(new MediaScanFailedEvent
                    {
                        MediaId = file.Id,
                        OwnerId = file.OwnerId,
                        FileName = file.FileName,
                        Reason = "Virus detected or scan failed.",
                    }, ct);
                }

                await tx2.CommitAsync(ct);
                logger.LogInformation("S3 event scan complete for {MediaId}: {Status}", msg.MediaId, file.Status);
            }
            catch (Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException)
            {
                logger.LogInformation("Concurrent scan for {MediaId} — already handled", msg.MediaId);
            }
            catch
            {
                await tx2.RollbackAsync(ct);
                throw;
            }
        }
        finally
        {
            try { File.Delete(tempPath); } catch (Exception ex) { logger.LogWarning(ex, "Failed to delete temporary file {TempPath}", tempPath); }
        }
    }
}
