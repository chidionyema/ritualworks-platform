using Haworks.Audit.Application.Extraction;

namespace Haworks.Audit.Application.Capture;

/// <summary>
/// Append-only writer for <c>audit_events</c>. L1.B implements this with
/// COPY-batched inserts (50 rows / 200ms threshold per spec § 5.4); L0
/// ships only the surface so L1.B's <c>AuditConsumer&lt;T&gt;</c> can
/// take a stable dependency.
/// </summary>
public interface IAuditWriter
{
    ValueTask WriteAsync(AuditRow row, CancellationToken ct);

    /// <summary>
    /// Drains the in-memory batch buffer. Called on shutdown so no rows
    /// are dropped between the last batch threshold and process exit.
    /// </summary>
    Task FlushAsync(CancellationToken ct);
}
