using System.Diagnostics;

namespace Haworks.Content.Application.Telemetry;

/// <summary>
/// ActivitySource for content-svc business spans. Registered in
/// BuildingBlocks ServiceDefaults so OpenTelemetry tracing picks it up.
/// </summary>
public static class ContentActivities
{
    public const string SourceName = "Haworks.Content";
    public static readonly ActivitySource Source = new(SourceName, "1.0.0");
}
