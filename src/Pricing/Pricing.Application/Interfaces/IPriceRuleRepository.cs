using Haworks.Pricing.Domain.Entities;

namespace Haworks.Pricing.Application.Interfaces;

/// <summary>
/// Repository for PriceRule aggregate.
/// </summary>
public interface IPriceRuleRepository
{
    Task<PriceRule?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<PriceRule>> GetActiveRulesForProductAsync(Guid productId, Guid? categoryId, int quantity, DateTimeOffset now, CancellationToken ct = default);
    Task<IReadOnlyList<PriceRule>> GetAllPagedAsync(Guid? productId, int page, int pageSize, CancellationToken ct = default);
    Task AddAsync(PriceRule rule, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
