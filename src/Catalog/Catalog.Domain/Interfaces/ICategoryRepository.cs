namespace Haworks.Catalog.Domain.Interfaces;

public interface ICategoryRepository
{
    Task<Category?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Category>> ListAsync(CancellationToken ct = default);
    Task AddAsync(Category category, CancellationToken ct = default);
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}
