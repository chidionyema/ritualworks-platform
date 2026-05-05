using System.Collections.Concurrent;
using Haworks.BffWeb.Application.Interfaces;

namespace Haworks.BffWeb.Api.Demo;

public sealed class DemoTraceStore : IDemoTraceStore
{
    private static readonly TimeSpan TraceTtl = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, (DemoTrace Trace, DateTime ExpiresAt)> _traces = new();

    public DemoTrace Record(DemoTrace trace)
    {
        EvictExpired();
        _traces[trace.TraceId] = (trace, DateTime.UtcNow.Add(TraceTtl));
        return trace;
    }

    public DemoTrace? Get(string traceId)
    {
        EvictExpired();
        return _traces.TryGetValue(traceId, out var entry) ? entry.Trace : null;
    }

    private void EvictExpired()
    {
        var now = DateTime.UtcNow;
        foreach (var kvp in _traces)
        {
            if (kvp.Value.ExpiresAt <= now)
            {
                _traces.TryRemove(kvp.Key, out _);
            }
        }
    }
}
