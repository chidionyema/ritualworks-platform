using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Haworks.Privacy.Application.Telemetry;

/// <summary>
/// ActivitySource for privacy-service saga business spans.
/// Registered in BuildingBlocks ServiceDefaults so OpenTelemetry tracing
/// picks it up.
/// </summary>
public static class PrivacyActivities
{
    public const string SourceName = "Haworks.Privacy";
    public static readonly ActivitySource Source = new(SourceName, "1.0.0");

    public static readonly Meter Meter = new(SourceName, "1.0.0");
    public static readonly Counter<long> ErasureStalled = Meter.CreateCounter<long>("privacy.erasure.stalled", description: "Privacy erasure requests stuck in Processing/Stalled state");
    public static readonly Counter<long> ErasureFailed = Meter.CreateCounter<long>("privacy.erasure.failed", description: "Privacy erasure requests that failed");
}
