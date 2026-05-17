using Haworks.Content.Application.Interfaces;
using Haworks.Content.Application.Options;
using Haworks.Content.Domain.Entities;
using Haworks.Content.Domain.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Haworks.Content.Infrastructure.BackgroundServices;

/// <summary>
/// Background sweeper for abandoned uploads. Finds <see cref="ContentStatus.Pending"/>
/// rows older than <see cref="StorageOptions.PendingUploadTtl"/>, aborts
/// their S3 multipart upload (if any), and marks the row Failed. Runs
/// every <see cref="StorageOptions.SweepInterval"/>; safe to run on
/// every replica concurrently because the row update uses xmin
/// optimistic concurrency.
/// </summary>
internal sealed class UploadSweeperService(
    IServiceScopeFactory scopeFactory,
    IOptions<StorageOptions> storageOptions,
    ILogger<UploadSweeperService> logger,
    TimeProvider time) : BackgroundService
{
    private const int BatchSize = 100;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = storageOptions.Value;
        logger.LogInformation(
            "UploadSweeper running every {Interval}, expiring Pending rows older than {Ttl}",
            opts.SweepInterval, opts.PendingUploadTtl);

        while (!stoppingToken.IsCancellationRequested)
        {
            await SweepTickSafeAsync(opts, stoppingToken);
        }
    }

    private async Task SweepTickSafeAsync(StorageOptions opts, CancellationToken stoppingToken)
    {
        try
        {
            await SweepOnceAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Sweeper iteration crashed; will retry on next tick");
        }

        try
        {
            await Task.Delay(opts.SweepInterval, time, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    internal async Task SweepOnceAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IContentRepository>();
        var storage = scope.ServiceProvider.GetRequiredService<IContentStorageService>();
        var opts = storageOptions.Value;

        var cutoff = time.GetUtcNow().UtcDateTime - opts.PendingUploadTtl;
        var batch = await repo.ListExpiredPendingAsync(cutoff, BatchSize, ct).ConfigureAwait(false);

        if (batch.Count == 0) return;

        logger.LogInformation("Sweeping {Count} expired Pending uploads (cutoff {Cutoff:O})", batch.Count, cutoff);

        foreach (var content in batch)
        {
            try
            {
                if (content.UploadKind == UploadKind.Multipart && !string.IsNullOrEmpty(content.S3UploadId))
                {
                    await storage.AbortMultipartUploadAsync(
                        content.ObjectName, content.S3UploadId, ct).ConfigureAwait(false);
                }
                content.Fail("Upload abandoned past TTL.");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Failed to abort S3 multipart for {ContentId}; marking row Failed regardless",
                    content.Id);
                content.Fail($"Upload abandoned past TTL; abort failed: {ex.Message}");
            }
        }

        await repo.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
