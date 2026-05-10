using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Haworks.Audit.Application.Export;
using Haworks.Audit.Infrastructure.Persistence;
using System.Threading.Channels;

namespace Haworks.Audit.Infrastructure.Export;

public class AuditExportJobService : IAuditExportJob
{
    private readonly AuditDbContext _db;
    private readonly ChannelWriter<Guid> _workerQueue;

    public AuditExportJobService(AuditDbContext db, ChannelWriter<Guid> workerQueue)
    {
        _db = db;
        _workerQueue = workerQueue;
    }

    public async Task<Guid> EnqueueAsync(AuditExportRequest request, string requestedBy, CancellationToken ct)
    {
        var job = new AuditExportJob
        {
            Id = Guid.NewGuid(),
            Status = AuditExportStatus.Queued,
            RequestedBy = requestedBy,
            RequestJson = JsonSerializer.SerializeToDocument(request)
        };

        _db.AuditExportJobs.Add(job);
        await _db.SaveChangesAsync(ct);

        await _workerQueue.WriteAsync(job.Id, ct);

        return job.Id;
    }

    public async Task<AuditExportJobSnapshot> GetStatusAsync(Guid jobId, CancellationToken ct)
    {
        var job = await _db.AuditExportJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job == null) return null!;

        return new AuditExportJobSnapshot(
            job.Id,
            job.Status,
            job.StartedAt,
            job.CompletedAt,
            job.DownloadUrl,
            job.Error);
    }
}
