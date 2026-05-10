namespace Haworks.Audit.Application.Export;

/// <summary>
/// Lifecycle of an audit-export job. Transitions are linear:
/// Queued → Running → (Succeeded | Failed). No retries at this phase
/// — failed exports require operator inspection.
/// </summary>
public enum AuditExportStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
}
