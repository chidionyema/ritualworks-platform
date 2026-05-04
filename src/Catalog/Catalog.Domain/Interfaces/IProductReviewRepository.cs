namespace Haworks.Catalog.Domain.Interfaces;

public interface IProductReviewRepository
{
    Task<ProductReview?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<ProductReview>> ListByProductIdAsync(Guid productId, int skip, int take, CancellationToken ct = default);
    Task AddAsync(ProductReview review, CancellationToken ct = default);
    Task UpdateAsync(ProductReview review, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}
