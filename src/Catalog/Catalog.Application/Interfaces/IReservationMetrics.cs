namespace Haworks.Catalog.Application.Interfaces;

/// <summary>
/// Hooks for the catalog reservation lifecycle that observability sinks
/// (OpenTelemetry, Prometheus, etc.) can implement. The v1 default
/// implementation is the no-op <c>NullReservationMetrics</c> in the
/// Infrastructure layer — a real OTel-backed implementation comes later
/// in the cross-service observability initiative.
/// </summary>
public interface IReservationMetrics
{
    /// <summary>
    /// Recorded once per reservation that the B3 sweeper transitions from
    /// <c>Pending</c> to <c>Expired</c>.
    /// </summary>
    void RecordReservationExpiredBySweeper();

    /// <summary>
    /// How long a reservation lived before reaching its terminal status.
    /// </summary>
    /// <param name="duration">Wall-clock time from reservation creation to terminal state.</param>
    /// <param name="terminalStatus">Lower-case terminal state (<c>"expired"</c>, <c>"confirmed"</c>).</param>
    void RecordReservationHoldDuration(TimeSpan duration, string terminalStatus);
}
