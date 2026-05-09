using System.ComponentModel.DataAnnotations;

namespace Haworks.Catalog.Application.Options;

/// <summary>
/// Tunables for B3's <c>ReservationSweeperService</c>. Bound from
/// configuration section <see cref="SectionName"/> on host start. The
/// defaults match ADR-004's "1 minute / batch of 200" recommendation —
/// short enough that abandoned-cart inventory becomes visible to other
/// shoppers within ~1 minute of expiry, batched enough that a single
/// sweep transaction stays bounded under load.
/// </summary>
public sealed class ReservationSweeperOptions
{
    public const string SectionName = "Reservations:Sweeper";

    /// <summary>How often the sweeper wakes up to scan for expired reservations.</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Hard cap on rows touched per sweep — keeps the per-iteration transaction bounded.</summary>
    [Range(1, 10_000)]
    public int BatchSize { get; set; } = 200;
}
