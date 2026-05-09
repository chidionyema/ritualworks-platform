using System.Diagnostics;

namespace Haworks.BffWeb.Application.Telemetry;

/// <summary>
/// ActivitySource for bff-web business spans. The BFF is the parent of
/// most cross-service traces (browser → BFF → backend), so its span
/// becomes the trace root for end-to-end checkout flows.
/// </summary>
public static class BffWebActivities
{
    public const string SourceName = "Haworks.BffWeb";
    public static readonly ActivitySource Source = new(SourceName, "1.0.0");
}
