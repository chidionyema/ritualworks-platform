using System.Diagnostics;

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
}
