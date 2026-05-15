using System.Diagnostics.Metrics;

namespace Haworks.Analytics.Api.Infrastructure.Telemetry;

public static class AnalyticsMetrics
{
    public const string MeterName = "Haworks.Analytics";
    private static readonly Meter Meter = new(MeterName);

    public static readonly Counter<long> EventsEnqueued = Meter.CreateCounter<long>(
        "analytics.events.enqueued", 
        description: "Total number of clickstream events received");

    public static readonly Counter<long> EventsDropped = Meter.CreateCounter<long>(
        "analytics.events.dropped", 
        description: "Number of events dropped due to buffer overflow");

    public static readonly ObservableGauge<int> BufferOccupancy = Meter.CreateObservableGauge(
        "analytics.buffer.occupancy", 
        () => 0, // Simplified for this implementation
        description: "Current number of events waiting in the memory buffer");
}
