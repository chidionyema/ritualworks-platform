using System.Diagnostics;
using Serilog.Core;
using Serilog.Events;

namespace Haworks.BuildingBlocks.Logging;

/// <summary>
/// Serilog enricher that adds trace_id and span_id from the current Activity
/// to every log event. Enables Loki → Tempo drill-down in Grafana.
///
/// Usage: .Enrich.With(new ActivityEnricher())
/// Or via generic: .Enrich.With&lt;ActivityEnricher&gt;()
/// </summary>
public sealed class ActivityEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        var activity = Activity.Current;
        if (activity is null) return;

        logEvent.AddPropertyIfAbsent(
            propertyFactory.CreateProperty("trace_id", activity.TraceId.ToHexString()));
        logEvent.AddPropertyIfAbsent(
            propertyFactory.CreateProperty("span_id", activity.SpanId.ToHexString()));
    }
}
