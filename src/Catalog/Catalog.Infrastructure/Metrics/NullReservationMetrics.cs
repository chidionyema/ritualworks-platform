using Haworks.Catalog.Application.Interfaces;

namespace Haworks.Catalog.Infrastructure.Metrics;

/// <summary>
/// No-op default for <see cref="IReservationMetrics"/>. Lets the sweeper
/// run without an observability stack wired in — the cross-service OTel
/// initiative ships a real implementation later.
/// </summary>
internal sealed class NullReservationMetrics : IReservationMetrics
{
    public void RecordReservationExpiredBySweeper()
    {
        // intentionally empty
    }

    public void RecordReservationHoldDuration(TimeSpan duration, string terminalStatus)
    {
        // intentionally empty
    }
}
