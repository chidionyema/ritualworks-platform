namespace Haworks.BffWeb.Application.Interfaces;

public sealed record DemoSpan(
    string SpanId,
    string? ParentSpanId,
    string Service,
    string Operation,
    long StartMs,
    long DurationMs,
    string Status,
    IReadOnlyDictionary<string, object> Attributes);

public sealed record DemoTrace(
    string TraceId,
    string RootSpanId,
    long DurationMs,
    IReadOnlyList<DemoSpan> Spans);

/// <summary>
/// Backs /api/traces/{traceId} for the portfolio's TraceViewer disclosure.
/// Phase 1: synthetic traces stamped by /api/demo/tracing/start. Phase 2:
/// could be wired to OpenTelemetry's in-memory exporter to show real
/// cross-service spans collected from the live microservices.
/// </summary>
public interface IDemoTraceStore
{
    DemoTrace Record(DemoTrace trace);
    DemoTrace? Get(string traceId);
}
