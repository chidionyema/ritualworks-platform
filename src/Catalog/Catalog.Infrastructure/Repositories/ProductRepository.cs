using System.Text.Json;
using Haworks.Contracts.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Haworks.Catalog.Infrastructure.Repositories;

internal sealed class ProductRepository(CatalogDbContext db) : IProductRepository
{
    public Task<Product?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        db.Products
            .AsNoTracking()
            .Include(p => p.Category)
            .Include(p => p.Metadata)
            .Include(p => p.Specifications)
            .FirstOrDefaultAsync(p => p.Id == id, ct);

    public Task<Product?> GetByIdTrackedAsync(Guid id, CancellationToken ct = default) =>
        db.Products
            .Include(p => p.Metadata)
            .Include(p => p.Specifications)
            .FirstOrDefaultAsync(p => p.Id == id, ct);

    public async Task<IReadOnlyList<Product>> ListAsync(
        int skip,
        int take,
        Guid? categoryId,
        CancellationToken ct = default)
    {
        var query = db.Products.AsNoTracking().Where(p => p.IsListed);
        if (categoryId.HasValue)
        {
            query = query.Where(p => p.CategoryId == categoryId.Value);
        }

        return await query
            .OrderBy(p => p.Name)
            .Skip(skip)
            .Take(take)
            .ToListAsync(ct);
    }

    public Task<int> CountAsync(Guid? categoryId, CancellationToken ct = default)
    {
        var query = db.Products.AsNoTracking().Where(p => p.IsListed);
        if (categoryId.HasValue)
        {
            query = query.Where(p => p.CategoryId == categoryId.Value);
        }
        return query.CountAsync(ct);
    }

    public async Task AddAsync(Product product, CancellationToken ct = default)
    {
        await db.Products.AddAsync(product, ct);
    }

    public Task UpdateAsync(Product product, CancellationToken ct = default)
    {
        // EF tracks changes automatically via the change-tracker when the
        // entity is loaded with GetByIdTrackedAsync; explicit Update marks
        // the entire entity as Modified for the detached-entity case.
        db.Products.Update(product);
        return Task.CompletedTask;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var existing = await db.Products.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (existing is not null) db.Products.Remove(existing);
    }

    public async Task AddStockReservationAsync(StockReservation reservation, CancellationToken ct = default)
    {
        await db.StockReservations.AddAsync(reservation, ct);
    }

    public Task<StockReservation?> GetStockReservationByOrderIdAsync(Guid orderId, CancellationToken ct = default) =>
        db.StockReservations.FirstOrDefaultAsync(r => r.OrderId == orderId, ct);

    public async Task<StockReservation> CreateReservationAsync(
        string userId,
        IReadOnlyList<StockReservationItem> items,
        TimeSpan ttl,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0) throw new ArgumentException("At least one reservation item is required.", nameof(items));

        // Aggregate by ProductId so the same product appearing twice in the
        // request collapses to a single decrement.
        var aggregated = items
            .GroupBy(i => i.ProductId)
            .Select(g => new StockReservationItem
            {
                ProductId = g.Key,
                ProductName = g.First().ProductName,
                Quantity = g.Sum(i => i.Quantity),
            })
            .Where(i => i.Quantity > 0)
            .ToList();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            foreach (var item in aggregated)
            {
                // Atomic per-product decrement guarded by a stock-check in the
                // WHERE clause. rowsAffected == 0 means either the product
                // doesn't exist or stock would go negative — both surface as
                // InsufficientStockException to the caller.
                var rows = await db.Products
                    .Where(p => p.Id == item.ProductId && p.StockQuantity >= item.Quantity)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(p => p.StockQuantity, p => p.StockQuantity - item.Quantity)
                        .SetProperty(p => p.IsInStock, p => p.StockQuantity - item.Quantity > 0), ct);

                if (rows == 0)
                {
                    var available = await db.Products
                        .AsNoTracking()
                        .Where(p => p.Id == item.ProductId)
                        .OrderBy(p => p.Id)
                        .Select(p => (int?)p.StockQuantity)
                        .FirstOrDefaultAsync(ct) ?? 0;

                    await tx.RollbackAsync(ct);
                    throw new InsufficientStockException(item.ProductId, item.Quantity, available);
                }
            }

            var itemsJson = JsonSerializer.Serialize(aggregated);
            var reservation = StockReservation.Create(userId, itemsJson, ttl);
            await db.StockReservations.AddAsync(reservation, ct);

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return reservation;
        }
        finally
        {
            // EF disposes the txn on dispose; explicit rollback above for
            // the InsufficientStockException path means double-rollback is
            // safe (BeginTransactionAsync no-ops after commit/rollback).
        }
    }

    public Task<StockReservation?> GetReservationByIdTrackedAsync(Guid id, CancellationToken ct = default) =>
        db.StockReservations.FirstOrDefaultAsync(r => r.Id == id, ct);

    public async Task<IReadOnlyList<StockReservation>> ListExpiredReservationsAsync(
        DateTime now,
        int batchSize,
        CancellationToken ct = default)
    {
        if (batchSize <= 0) return Array.Empty<StockReservation>();

        return await db.StockReservations
            .Where(r => r.Status == ReservationStatus.Pending && r.ExpiresAt <= now)
            .OrderBy(r => r.ExpiresAt)
            .Take(batchSize)
            .ToListAsync(ct);
    }

    public Task<int> SaveChangesAsync(CancellationToken ct = default) =>
        db.SaveChangesAsync(ct);
}
