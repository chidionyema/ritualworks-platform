using System.Diagnostics;

namespace Haworks.Search.Application.Telemetry;

/// <summary>
/// ActivitySource for search-svc business spans. Registered in
/// BuildingBlocks ServiceDefaults so OpenTelemetry tracing picks it up.
/// </summary>
public static class SearchActivities
{
    public const string SourceName = "Haworks.Search";
    public static readonly ActivitySource Source = new(SourceName, "1.0.0");
}
