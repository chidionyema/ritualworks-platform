namespace Haworks.Audit.Application.Export;

/// <summary>
/// Async export pipeline. <see cref="EnqueueAsync"/> persists a job row
/// + signals the worker; the worker (L1.D) streams matching audit rows
/// to a CSV file in S3 and surfaces a signed URL via
/// <see cref="GetStatusAsync"/>.
///
/// L1.D ships the implementation; L0 ships the surface so the controller
/// (also L1.D, but landed in a separate group) can take a stable
/// dependency.
/// </summary>
public interface IAuditExportJob
{
    Task<Guid> EnqueueAsync(AuditExportRequest request, string requestedBy, CancellationToken ct);

    Task<AuditExportJobSnapshot> GetStatusAsync(Guid jobId, CancellationToken ct);
}

public sealed record AuditExportJobSnapshot(
    Guid JobId,
    AuditExportStatus Status,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? DownloadUrl,
    string? Error);
