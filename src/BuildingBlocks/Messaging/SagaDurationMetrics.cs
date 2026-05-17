using System.Diagnostics.Metrics;

namespace Haworks.BuildingBlocks.Messaging;

/// <summary>
/// Records saga end-to-end duration when a saga reaches a terminal state.
/// Enables SLO tracking: "99% of checkouts complete in under 30s".
///
/// Usage in saga state machine:
///   .Then(ctx => SagaDurationMetrics.RecordCompletion("checkout", ctx.Saga.CreatedAt, "completed"))
/// </summary>
public static class SagaDurationMetrics
{
    private static readonly Meter Meter = new("Haworks.Sagas", "1.0.0");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>(
        "saga.duration.seconds",
        unit: "s",
        description: "End-to-end saga duration from initiation to terminal state");

    public static void RecordCompletion(string sagaName, DateTime createdAtUtc, string terminalState)
    {
        var elapsed = (DateTime.UtcNow - createdAtUtc).TotalSeconds;
        Duration.Record(elapsed,
            new KeyValuePair<string, object?>("saga_name", sagaName),
            new KeyValuePair<string, object?>("terminal_state", terminalState));
    }
}
