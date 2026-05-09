using System.Diagnostics;

namespace Haworks.Orders.Application.Telemetry;

/// <summary>
/// ActivitySource for orders-svc business spans. Registered in
/// BuildingBlocks ServiceDefaults so OpenTelemetry tracing picks it up.
/// </summary>
public static class OrdersActivities
{
    public const string SourceName = "Haworks.Orders";
    public static readonly ActivitySource Source = new(SourceName, "1.0.0");
}
