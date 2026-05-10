using System.Diagnostics;

namespace Haworks.CheckoutOrchestrator.Application.Telemetry;

/// <summary>
/// ActivitySource for checkout-orchestrator (saga) business spans.
/// Registered in BuildingBlocks ServiceDefaults so OpenTelemetry tracing
/// picks it up.
/// </summary>
public static class CheckoutActivities
{
    public const string SourceName = "Haworks.CheckoutOrchestrator";
    public static readonly ActivitySource Source = new(SourceName, "1.0.0");
}
