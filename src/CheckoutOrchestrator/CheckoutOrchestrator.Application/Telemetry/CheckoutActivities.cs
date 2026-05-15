using System.Diagnostics;
using System.Diagnostics.Metrics;

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

    public static readonly Meter Meter = new(SourceName, "1.0.0");
    public static readonly Counter<long> CheckoutStuckInReview = Meter.CreateCounter<long>("checkout.saga.stuck_in_review", description: "Checkout sagas stuck in RequiresReview state");
    public static readonly Counter<long> CheckoutAbandoned = Meter.CreateCounter<long>("checkout.saga.abandoned", description: "Checkout sagas abandoned due to payment expiry");
}
