using Haworks.BuildingBlocks.Common;
using Haworks.Contracts.Catalog;

namespace Haworks.Catalog.Domain.Interfaces;

public interface IProductRepository
{
    Task<Product?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<Product?> GetByIdTrackedAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Product>> ListAsync(int skip, int take, Guid? categoryId, CancellationToken ct = default);
    Task<int> CountAsync(Guid? categoryId, CancellationToken ct = default);
    Task AddAsync(Product product, CancellationToken ct = default);
    Task UpdateAsync(Product product, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    Task<int> SaveChangesAsync(CancellationToken ct = default);

    // Stock reservation methods (renamed entity is StockReservation; the
    // saga path keeps using Add+Get with the Confirmed factory).
    Task AddStockReservationAsync(StockReservation reservation, CancellationToken ct = default);
    Task<StockReservation?> GetStockReservationByOrderIdAsync(Guid orderId, CancellationToken ct = default);

    // B1 lifecycle additions:

    /// <summary>
    /// Atomically decrements <see cref="Product.StockQuantity"/> for each
    /// item AND inserts a Pending <see cref="StockReservation"/> row, all
    /// in one EF transaction. Throws
    /// <see cref="InsufficientStockException"/> on the first product that
    /// would go negative.
    /// </summary>
    Task<StockReservation> CreateReservationAsync(
        string userId,
        IReadOnlyList<StockReservationItem> items,
        TimeSpan ttl,
        CancellationToken ct = default);

    /// <summary>Tracked load by reservation id (Confirm path).</summary>
    Task<StockReservation?> GetReservationByIdTrackedAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Lists Pending reservations whose <c>ExpiresAt</c> is at or before
    /// <paramref name="now"/>. Used by B3's
    /// <c>ReservationSweeperService</c>; the
    /// <c>(Status, ExpiresAt)</c> index covers this query.
    /// </summary>
    Task<IReadOnlyList<StockReservation>> ListExpiredReservationsAsync(
        DateTime now,
        int batchSize,
        CancellationToken ct = default);
}
