using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using CsvHelper;
using Microsoft.EntityFrameworkCore;
using Haworks.Audit.Application.Export;
using Haworks.Audit.Infrastructure.Persistence;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Haworks.Audit.Infrastructure.Export;

public class AuditExportWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ChannelReader<Guid> _queue;
    private readonly IAmazonS3 _s3;
    private readonly ILogger<AuditExportWorker> _logger;

    public AuditExportWorker(
        IServiceProvider serviceProvider,
        ChannelReader<Guid> queue,
        IAmazonS3 s3,
        ILogger<AuditExportWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _queue = queue;
        _s3 = s3;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Poll for any stranded jobs that were queued but not picked up (e.g. due to crash)
        _ = Task.Run(() => PollStrandedJobsAsync(stoppingToken), stoppingToken);

        await foreach (var jobId in _queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessJobAsync(jobId, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process export job {JobId}", jobId);
            }
        }
    }

    private async Task PollStrandedJobsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
                
                var strandedJobIds = await db.AuditExportJobs
                    .Where(j => j.Status == AuditExportStatus.Queued)
                    .OrderBy(j => j.Id)
                    .Select(j => j.Id)
                    .Take(100)
                    .ToListAsync(ct);
                    
                foreach (var id in strandedJobIds)
                {
                    try
                    {
                        await ProcessJobAsync(id, ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to process stranded export job {JobId}", id);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error polling stranded jobs");
            }

            await Task.Delay(TimeSpan.FromMinutes(1), ct);
        }
    }

    private async Task ProcessJobAsync(Guid jobId, CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuditDbContext>();

        var job = await db.AuditExportJobs.FindAsync(new object[] { jobId }, ct);
        if (job == null || job.Status != AuditExportStatus.Queued) return;

        job.Status = AuditExportStatus.Running;
        job.StartedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        try
        {
            var request = JsonSerializer.Deserialize<AuditExportRequest>(job.RequestJson.RootElement.GetRawText());
            if (request == null) throw new InvalidOperationException("Invalid request JSON");

            var tempFile = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            try
            {
                await using (var writer = new StreamWriter(tempFile))
                await using (var csv = new CsvWriter(writer, CultureInfo.InvariantCulture))
                {
                    var query = db.AuditEvents.AsNoTracking()
                        .Where(e => e.OccurredAt >= request.From && e.OccurredAt <= request.To);

                    if (!string.IsNullOrEmpty(request.EntityId))
                        query = query.Where(e => e.EntityId == request.EntityId);
                    if (!string.IsNullOrEmpty(request.EntityType))
                        query = query.Where(e => e.EntityType == request.EntityType);
                    if (!string.IsNullOrEmpty(request.EventType))
                        query = query.Where(e => e.EventType == request.EventType);

                    var rows = query.OrderBy(e => e.OccurredAt).AsAsyncEnumerable();

                    csv.WriteHeader<AuditExportRow>();
                    await csv.NextRecordAsync();

                    await foreach (var row in rows.WithCancellation(ct))
                    {
                        csv.WriteRecord(new AuditExportRow
                        {
                            Id = row.Id,
                            OccurredAt = row.OccurredAt,
                            EventType = row.EventType,
                            EntityType = row.EntityType,
                            EntityId = row.EntityId,
                            ActorId = row.ActorId,
                            ActorType = row.ActorType,
                            CorrelationId = row.CorrelationId,
                            Payload = row.Payload.RootElement.GetRawText(),
                            Metadata = row.Metadata.RootElement.GetRawText()
                        });
                        await csv.NextRecordAsync();
                    }
                }

                var key = $"exports/{jobId}.csv";
                await using (var stream = File.OpenRead(tempFile))
                {
                    await _s3.PutObjectAsync(new PutObjectRequest
                    {
                        BucketName = "audit-exports",
                        Key = key,
                        InputStream = stream,
                        ContentType = "text/csv"
                    }, ct);
                }

                job.DownloadUrl = _s3.GetPreSignedURL(new GetPreSignedUrlRequest
                {
                    BucketName = "audit-exports",
                    Key = key,
                    Expires = DateTime.UtcNow.AddHours(24)
                });
                job.Status = AuditExportStatus.Succeeded;
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }
        catch (Exception ex)
        {
            job.Status = AuditExportStatus.Failed;
            job.Error = ex.Message;
        }

        job.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private sealed class AuditExportRow
    {
        public Guid Id { get; set; }
        public DateTimeOffset OccurredAt { get; set; }
        public string EventType { get; set; } = string.Empty;
        public string EntityType { get; set; } = string.Empty;
        public string EntityId { get; set; } = string.Empty;
        public string? ActorId { get; set; }
        public string? ActorType { get; set; }
        public string? CorrelationId { get; set; }
        public string Payload { get; set; } = string.Empty;
        public string Metadata { get; set; } = string.Empty;
    }
}
