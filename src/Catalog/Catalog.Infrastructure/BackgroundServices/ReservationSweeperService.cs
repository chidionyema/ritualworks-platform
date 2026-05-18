using System.Text.Json;
using Haworks.Catalog.Application.Interfaces;
using Haworks.Catalog.Application.Options;
using Haworks.Contracts.Catalog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Haworks.Catalog.Infrastructure.BackgroundServices;

/// <summary>
/// ADR-004 / B3 sweeper for Pending <see cref="StockReservation"/> rows
/// whose <c>ExpiresAt</c> has passed.
///
/// For each match: atomically transitions the reservation to
/// <see cref="ReservationStatus.Expired"/> and returns its held stock to
/// inventory via <see cref="IStockService.ReleaseStockAsync(IEnumerable{StockReservationItem}, CancellationToken)"/>.
/// The aggregate's <see cref="StockReservation.Expire"/> guard (Status must
/// be Pending) prevents double-release if a confirm lands at the same
/// instant — the loser of the race becomes a no-op.
///
/// Order-of-operations is load-bearing: the aggregate state transition runs
/// BEFORE the stock release, so a partial failure (DB up, stock service
/// blip) leaves the reservation Pending and re-tryable on the next sweep
/// rather than half-released.
/// </summary>
internal sealed class ReservationSweeperService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<ReservationSweeperOptions> _options;
    private readonly ILogger<ReservationSweeperService> _logger;

    public ReservationSweeperService(
        IServiceScopeFactory scopeFactory,
        IOptions<ReservationSweeperOptions> options,
        ILogger<ReservationSweeperService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var sweepInterval = _options.Value.SweepInterval;
            var batchSize = _options.Value.BatchSize;

            _logger.LogInformation(
                "Reservation sweeper started. Interval={Interval}, Batch={BatchSize}",
                sweepInterval, batchSize);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var expired = await SweepOnceAsync(stoppingToken).ConfigureAwait(false);
                    if (expired > 0)
                    {
                        _logger.LogInformation("Sweeper expired {Count} reservations", expired);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Resilient loop: log and keep going so a transient DB blip
                    // doesn't wedge the sweeper forever.
                    _logger.LogError(ex, "Reservation sweep failed; will retry next interval");
                }

                try
                {
                    await Task.Delay(_options.Value.SweepInterval, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            _logger.LogInformation("Reservation sweeper stopped");
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Unhandled exception in background service {ServiceName}", nameof(ReservationSweeperService));
            throw;
        }
    }

    /// <summary>
    /// Single sweep iteration. Visible for tests so they can drive expiry
    /// deterministically without spinning up the BackgroundService timer.
    /// Returns the number of reservations actually expired (i.e., the
    /// aggregate accepted the transition AND the stock release succeeded).
    /// </summary>
    internal async Task<int> SweepOnceAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var repo = sp.GetRequiredService<IProductRepository>();
        var metrics = sp.GetRequiredService<IReservationMetrics>();

        var batchSize = _options.Value.BatchSize;
        if (batchSize <= 0)
        {
            return 0;
        }

        var now = DateTime.UtcNow;
        var candidates = await repo
            .ListExpiredReservationsAsync(now, batchSize, ct)
            .ConfigureAwait(false);

        if (candidates.Count == 0)
        {
            return 0;
        }

        var expiredCount = 0;
        foreach (var reservation in candidates)
        {
            try
            {
                // Aggregate transition first — its guard enforces "must be
                // Pending". Doing this before the stock release means a
                // partial failure leaves the reservation Pending and
                // re-tryable on the next sweep.
                if (!reservation.Expire())
                {
                    // Raced with confirm or another sweeper — skip and move on.
                    _logger.LogDebug(
                        "Skipped reservation {ReservationId}: aggregate refused Expire (likely raced)",
                        reservation.Id);
                    continue;
                }

                // Release stock INLINE (same unit of work) instead of calling
                // stockService.ReleaseStockAsync which commits its own
                // transaction. This ensures reservation expiry + stock
                // increment are atomic — no crash window for double-release.
                var items = ParseItems(reservation.ItemsJson);
                foreach (var item in items)
                {
                    if (item.Quantity <= 0) continue;

                    var product = await repo.GetByIdTrackedAsync(item.ProductId, ct).ConfigureAwait(false);
                    if (product is null)
                    {
                        _logger.LogWarning(
                            "Reservation {ReservationId} references unknown product {ProductId}; skipping item",
                            reservation.Id, item.ProductId);
                        continue;
                    }

                    product.ReleaseStock(item.Quantity);
                }

                reservation.MarkReleased("sweeper_expired");

                metrics.RecordReservationExpiredBySweeper();
                metrics.RecordReservationHoldDuration(
                    DateTime.UtcNow - reservation.ReservedAt,
                    terminalStatus: "expired");
                expiredCount++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Caller is shutting down — bubble up so ExecuteAsync exits.
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to expire reservation {ReservationId}; will retry next sweep",
                    reservation.Id);
            }
        }

        // Single SaveChanges commits all reservation expirations + stock
        // increments atomically.
        await repo.SaveChangesAsync(ct).ConfigureAwait(false);
        return expiredCount;
    }

    private static IReadOnlyList<StockReservationItem> ParseItems(string itemsJson)
    {
        if (string.IsNullOrWhiteSpace(itemsJson))
        {
            return Array.Empty<StockReservationItem>();
        }

        var items = JsonSerializer.Deserialize<List<StockReservationItem>>(itemsJson);
        return items ?? new List<StockReservationItem>();
    }
}
