using System.Diagnostics;

namespace Haworks.Payments.Application.Telemetry;

/// <summary>
/// ActivitySource for payments-svc business spans. Registered in
/// BuildingBlocks ServiceDefaults so OpenTelemetry tracing picks it up.
/// </summary>
public static class PaymentsActivities
{
    public const string SourceName = "Haworks.Payments";
    public static readonly ActivitySource Source = new(SourceName, "1.0.0");
}
