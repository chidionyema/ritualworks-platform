using System.Diagnostics;
using System.Diagnostics.Metrics;
using Haworks.Catalog.Application.Interfaces;

namespace Haworks.Catalog.Infrastructure.Metrics;

/// <summary>
/// Real implementation of <see cref="IReservationMetrics"/> using System.Diagnostics.Metrics.
/// Compatible with OpenTelemetry and .NET Aspire dashboard.
/// </summary>
internal sealed class CatalogReservationMetrics : IReservationMetrics
{
    private readonly Counter<long> _expiredCounter;
    private readonly Histogram<double> _holdDuration;

    public CatalogReservationMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create("Haworks.Catalog");

        _expiredCounter = meter.CreateCounter<long>(
            "catalog.reservations.expired.count",
            unit: "{reservation}",
            description: "Number of stock reservations that expired and were released by the sweeper");

        _holdDuration = meter.CreateHistogram<double>(
            "catalog.reservations.hold.duration",
            unit: "s",
            description: "Duration a reservation was held before reaching a terminal state (confirmed or expired)");
    }

    public void RecordReservationExpiredBySweeper()
    {
        _expiredCounter.Add(1);
    }

    public void RecordReservationHoldDuration(TimeSpan duration, string terminalStatus)
    {
        _holdDuration.Record(duration.TotalSeconds, new TagList { { "status", terminalStatus } });
    }
}
