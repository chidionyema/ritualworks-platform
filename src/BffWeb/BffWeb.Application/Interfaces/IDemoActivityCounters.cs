namespace Haworks.BffWeb.Application.Interfaces;

public sealed record DemoActivitySnapshot(
    long IngressEvents24h,
    int ActiveSessions,
    double P99LatencyMs,
    DateTime CapturedAt);

/// <summary>
/// Real activity counters for the portfolio's hero-tile metrics. Every
/// <c>/api/demo/*</c> request increments <c>IngressEvents24h</c> via
/// <c>DemoActivityMiddleware</c>. Active sessions are tracked as a sliding
/// window of recent <c>X-Demo-Session</c> header values. P99 latency is
/// computed from a rolling histogram of per-request durations.
///
/// Replaces the Phase 1 fakes — the snapshot fields now reflect this BffWeb
/// instance's actual activity since process start, no synthetic literals.
/// </summary>
public interface IDemoActivityCounters
{
    void RecordRequest(string? sessionId, double durationMs);
    DemoActivitySnapshot Snapshot();
}
