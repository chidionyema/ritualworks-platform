using System.Diagnostics.Metrics;
using Haworks.Analytics.Api.Infrastructure.Buffer;

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

    public static readonly Counter<long> EventsFlushed = Meter.CreateCounter<long>(
        "analytics.events.flushed",
        description: "Total number of events successfully produced to Kafka");

    // Populated once the buffer singleton is registered; set via Configure().
    private static IEventBuffer? _buffer;

    public static readonly ObservableGauge<int> BufferOccupancy = Meter.CreateObservableGauge(
        "analytics.buffer.occupancy",
        () => _buffer?.Count ?? 0,
        description: "Current number of events waiting in the memory buffer");

    /// <summary>Called from DI after the buffer singleton is created.</summary>
    public static void Configure(IEventBuffer buffer) => _buffer = buffer;
}
