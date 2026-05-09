using System.Diagnostics;

namespace Haworks.Identity.Application.Telemetry;

/// <summary>
/// ActivitySource for identity-svc business spans. Registered in
/// BuildingBlocks ServiceDefaults so OpenTelemetry tracing picks it up.
/// </summary>
public static class IdentityActivities
{
    public const string SourceName = "Haworks.Identity";
    public static readonly ActivitySource Source = new(SourceName, "1.0.0");
}
