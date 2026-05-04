using Haworks.BuildingBlocks.Common;

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
    
    // Stock methods
    Task AddOrderStockReservationAsync(OrderStockReservation reservation, CancellationToken ct = default);
    Task<OrderStockReservation?> GetOrderStockReservationAsync(Guid orderId, CancellationToken ct = default);
}
