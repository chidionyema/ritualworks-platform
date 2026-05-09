using System.Diagnostics;

namespace Haworks.Catalog.Application.Telemetry;

/// <summary>
/// ActivitySource for catalog-svc business spans. Registered in
/// BuildingBlocks ServiceDefaults so OpenTelemetry tracing picks it up.
/// Wrap the highest-leverage handlers (e.g. stock reservation) with
/// <c>using var activity = CatalogActivities.Source.StartActivity(...);</c>
/// to give Tempo traces a business story instead of just HTTP/DB spans.
/// </summary>
public static class CatalogActivities
{
    public const string SourceName = "Haworks.Catalog";
    public static readonly ActivitySource Source = new(SourceName, "1.0.0");
}
