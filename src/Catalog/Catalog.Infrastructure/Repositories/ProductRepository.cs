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

    public async Task AddOrderStockReservationAsync(OrderStockReservation reservation, CancellationToken ct = default)
    {
        await db.OrderStockReservations.AddAsync(reservation, ct);
    }

    public Task<OrderStockReservation?> GetOrderStockReservationAsync(Guid orderId, CancellationToken ct = default) =>
        db.OrderStockReservations.FirstOrDefaultAsync(r => r.OrderId == orderId, ct);

    public Task<int> SaveChangesAsync(CancellationToken ct = default) =>
        db.SaveChangesAsync(ct);
}
