namespace Haworks.Catalog.Domain.Interfaces;

/// <summary>
/// Catalog-context repository for Product aggregate. The Application layer
/// depends on this interface; Infrastructure provides the EF implementation.
/// All read methods are AsNoTracking-equivalent unless the caller explicitly
/// asks for the tracked variant (needed when mutating + SaveChangesAsync).
/// </summary>
public interface IProductRepository
{
    Task<Product?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<Product?> GetByIdTrackedAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Product>> ListAsync(int skip, int take, Guid? categoryId, CancellationToken ct = default);
    Task<int> CountAsync(Guid? categoryId, CancellationToken ct = default);
    Task AddAsync(Product product, CancellationToken ct = default);
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}
