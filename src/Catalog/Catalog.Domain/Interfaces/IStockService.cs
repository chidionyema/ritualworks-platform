using Haworks.BuildingBlocks.Common;
using Haworks.Contracts.Catalog;

namespace Haworks.Catalog.Domain.Interfaces;

public interface IStockService
{
    Task<Result> ReserveStockAsync(Guid orderId, IEnumerable<StockReservationItem> items, CancellationToken ct = default);
    Task ReleaseStockAsync(Guid orderId, string reason, CancellationToken ct = default);
}
