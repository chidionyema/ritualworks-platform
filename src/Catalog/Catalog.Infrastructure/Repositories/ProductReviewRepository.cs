using Microsoft.EntityFrameworkCore;
using Haworks.Catalog.Domain;
using Haworks.Catalog.Domain.Interfaces;

namespace Haworks.Catalog.Infrastructure.Repositories;

internal sealed class ProductReviewRepository(CatalogDbContext db) : IProductReviewRepository
{
    public Task<ProductReview?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        db.ProductReviews.FirstOrDefaultAsync(r => r.Id == id, ct);

    public async Task<IReadOnlyList<ProductReview>> ListByProductIdAsync(Guid productId, int skip, int take, CancellationToken ct = default) =>
        await db.ProductReviews
            .AsNoTracking()
            .Where(r => r.ProductId == productId)
            .OrderByDescending(r => r.CreatedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync(ct);

    public async Task AddAsync(ProductReview review, CancellationToken ct = default)
    {
        await db.ProductReviews.AddAsync(review, ct);
    }

    public Task UpdateAsync(ProductReview review, CancellationToken ct = default)
    {
        db.ProductReviews.Update(review);
        return Task.CompletedTask;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var existing = await db.ProductReviews.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (existing is not null) db.ProductReviews.Remove(existing);
    }

    public Task<int> SaveChangesAsync(CancellationToken ct = default) =>
        db.SaveChangesAsync(ct);
}
