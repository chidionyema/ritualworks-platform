using System.Collections.Concurrent;
using Haworks.BffWeb.Application.Interfaces;

namespace Haworks.BffWeb.Api.Demo;

/// <summary>
/// Singleton in-memory counters backing the portfolio's hero metrics tile.
/// Every number reflects this BffWeb instance's actual activity since
/// process start — no fake literals.
///
/// Counters:
/// <list type="bullet">
///   <item><c>IngressEvents24h</c> — count of <c>/api/demo/*</c> requests in a sliding 24h window.</item>
///   <item><c>ActiveSessions</c> — distinct <c>X-Demo-Session</c> ids seen in the last 15 minutes.</item>
///   <item><c>P99LatencyMs</c> — 99th percentile from a rolling 1024-entry ring buffer of per-request durations.</item>
/// </list>
/// </summary>
public sealed class DemoActivityCounters : IDemoActivityCounters
{
    private static readonly TimeSpan SessionWindow = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan RequestWindow = TimeSpan.FromHours(24);
    private const int HistogramSize = 1024;

    private readonly ConcurrentQueue<DateTime> _recentRequests = new();
    private readonly ConcurrentDictionary<string, DateTime> _activeSessions = new();
    // Lock-free ring buffer for the latency histogram. Newer values overwrite
    // oldest at the wrap. P99 is computed by sorting a snapshot copy.
    private readonly double[] _latencyRing = new double[HistogramSize];
    private long _ringIndex;
    private int _ringFilled;

    public void RecordRequest(string? sessionId, double durationMs)
    {
        var now = DateTime.UtcNow;

        _recentRequests.Enqueue(now);

        if (!string.IsNullOrEmpty(sessionId))
        {
            _activeSessions[sessionId] = now;
        }

        var slot = (int)((Interlocked.Increment(ref _ringIndex) - 1) % HistogramSize);
        _latencyRing[slot] = durationMs;
        if (_ringFilled < HistogramSize) Interlocked.Increment(ref _ringFilled);

        Prune(now);
    }

    public DemoActivitySnapshot Snapshot()
    {
        var now = DateTime.UtcNow;
        Prune(now);

        return new DemoActivitySnapshot(
            IngressEvents24h: _recentRequests.Count,
            ActiveSessions: _activeSessions.Count,
            P99LatencyMs: ComputeP99(),
            CapturedAt: now);
    }

    private void Prune(DateTime now)
    {
        var requestCutoff = now - RequestWindow;
        while (_recentRequests.TryPeek(out var oldest) && oldest < requestCutoff)
        {
            _recentRequests.TryDequeue(out _);
        }

        var sessionCutoff = now - SessionWindow;
        foreach (var kvp in _activeSessions)
        {
            if (kvp.Value < sessionCutoff)
            {
                _activeSessions.TryRemove(kvp.Key, out _);
            }
        }
    }

    private double ComputeP99()
    {
        var filled = Math.Min(_ringFilled, HistogramSize);
        if (filled == 0) return 0;

        var copy = new double[filled];
        Array.Copy(_latencyRing, copy, filled);
        Array.Sort(copy);

        var idx = (int)Math.Ceiling(filled * 0.99) - 1;
        if (idx < 0) idx = 0;
        if (idx >= filled) idx = filled - 1;
        return copy[idx];
    }
}
