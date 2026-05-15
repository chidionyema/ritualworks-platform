using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Haworks.Payments.Application.Telemetry;

/// <summary>
/// ActivitySource for payments-svc business spans. Registered in
/// BuildingBlocks ServiceDefaults so OpenTelemetry tracing picks it up.
/// </summary>
public static class PaymentsActivities
{
    public const string SourceName = "Haworks.Payments";
    public static readonly ActivitySource Source = new(SourceName, "1.0.0");

    public static readonly Meter Meter = new(SourceName, "1.0.0");
    public static readonly Counter<long> RefundStuckInReview = Meter.CreateCounter<long>("payments.refund.stuck_in_review", description: "Refund sagas stuck in RequiresReview state");
    public static readonly Counter<long> SubscriptionDunningExhausted = Meter.CreateCounter<long>("payments.subscription.dunning_exhausted", description: "Subscriptions canceled by dunning exhaustion");
}
