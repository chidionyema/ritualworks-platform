using Microsoft.EntityFrameworkCore;
using Haworks.Orders.Domain;
using Haworks.Orders.Domain.Interfaces;

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

    public async Task AddGuestInfoAsync(GuestOrderInfo guestInfo, CancellationToken ct = default) =>
        await db.GuestOrders.AddAsync(guestInfo, ct);

    public Task<GuestOrderInfo?> GetGuestInfoAsync(Guid orderId, CancellationToken ct = default) =>
        db.GuestOrders.FirstOrDefaultAsync(g => g.OrderId == orderId, ct);

    public Task<GuestOrderInfo?> GetGuestByTokenAsync(string token, CancellationToken ct = default) =>
        db.GuestOrders.FirstOrDefaultAsync(g => g.OrderToken == token, ct);

    public async Task<IReadOnlyList<Order>> GetAbandonedOrdersAsync(DateTime cutoffTime, int take = 100, CancellationToken ct = default)
    {
        return await db.Orders
            .Include(o => o.Items)
            .Where(o => o.Status == OrderStatus.Created && o.CreatedAt < cutoffTime)
            .Take(take)
            .ToListAsync(ct);
    }

    public async Task<bool> MarkStockReleasedAsync(Guid orderId, OrderStatus newStatus, string reason, CancellationToken ct = default)
    {
        var rowsAffected = await db.Orders
            .Where(o => o.Id == orderId && o.Status != newStatus)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(o => o.Status, newStatus)
                .SetProperty(o => o.AbandonReason, reason), ct);

        return rowsAffected > 0;
    }
}
