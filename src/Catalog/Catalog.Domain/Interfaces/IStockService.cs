using Haworks.BuildingBlocks.Common;
using Haworks.Contracts.Catalog;

namespace Haworks.Catalog.Domain.Interfaces;

public interface IStockService
{
    Task<Result> ReserveStockAsync(Guid orderId, IEnumerable<StockReservationItem> items, CancellationToken ct = default);
    Task ReleaseStockAsync(Guid orderId, string reason, CancellationToken ct = default);

    /// <summary>
    /// Returns previously-reserved units to inventory. Used by the
    /// confirm-failed-cleanup path and by B3's
    /// <c>ReservationSweeperService</c> when a Pending reservation
    /// expires. Wraps a single EF transaction.
    /// </summary>
    Task ReleaseStockAsync(IEnumerable<StockReservationItem> items, CancellationToken ct = default);
}
