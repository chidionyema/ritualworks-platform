namespace Haworks.Orders.Domain.Interfaces;

public interface IOrderRepository
{
    Task<Order?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<Order?> GetByIdTrackedAsync(Guid id, CancellationToken ct = default);
    Task<Order?> GetBySagaIdTrackedAsync(Guid sagaId, CancellationToken ct = default);
    Task<IReadOnlyList<Order>> ListByUserAsync(string userId, int skip, int take, CancellationToken ct = default);
    Task<int> CountByUserAsync(string userId, CancellationToken ct = default);
    Task AddAsync(Order order, CancellationToken ct = default);
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}
