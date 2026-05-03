using Microsoft.EntityFrameworkCore;

namespace Haworks.Orders.Infrastructure.Repositories;

internal sealed class OrderRepository(OrderDbContext db) : IOrderRepository
{
    public Task<Order?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        db.Orders.AsNoTracking()
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == id, ct);

    public Task<Order?> GetByIdTrackedAsync(Guid id, CancellationToken ct = default) =>
        db.Orders
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == id, ct);

    public Task<Order?> GetBySagaIdTrackedAsync(Guid sagaId, CancellationToken ct = default) =>
        db.Orders
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.SagaId == sagaId, ct);

    public async Task<IReadOnlyList<Order>> ListByUserAsync(string userId, int skip, int take, CancellationToken ct = default)
    {
        return await db.Orders.AsNoTracking()
            .Where(o => o.UserId == userId)
            .OrderByDescending(o => o.CreatedAt)
            .Skip(skip)
            .Take(take)
            .Include(o => o.Items)
            .ToListAsync(ct);
    }

    public Task<int> CountByUserAsync(string userId, CancellationToken ct = default) =>
        db.Orders.AsNoTracking().CountAsync(o => o.UserId == userId, ct);

    public async Task AddAsync(Order order, CancellationToken ct = default) =>
        await db.Orders.AddAsync(order, ct);

    public Task<int> SaveChangesAsync(CancellationToken ct = default) =>
        db.SaveChangesAsync(ct);
}
