using Haworks.Pricing.Application.Interfaces;
using Haworks.Pricing.Domain.Entities;
using Haworks.Pricing.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Haworks.Pricing.Infrastructure.Repositories;

/// <summary>
/// EF Core implementation of IPriceRuleRepository.
/// </summary>
public sealed class PriceRuleRepository : IPriceRuleRepository
{
    private readonly PricingDbContext _context;

    public PriceRuleRepository(PricingDbContext context)
    {
        _context = context;
    }

    public Task<PriceRule?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return _context.PriceRules
            .Include(r => r.TieredPrices)
            .FirstOrDefaultAsync(r => r.Id == id, ct);
    }

    public async Task<IReadOnlyList<PriceRule>> GetActiveRulesForProductAsync(
        Guid productId, Guid? categoryId, int quantity, DateTimeOffset now, CancellationToken ct = default)
    {
        return await _context.PriceRules
            .Include(r => r.TieredPrices)
            .Where(r => r.IsActive && !r.IsDeleted)
            .Where(r =>
                (r.ProductId == productId) ||
                (r.ProductId == null && r.CategoryId == categoryId) ||
                (r.ProductId == null && r.CategoryId == null))
            .Where(r => r.StartsAt == null || r.StartsAt <= now)
            .Where(r => r.ExpiresAt == null || r.ExpiresAt > now)
            .Where(r => r.MinimumQuantity <= quantity)
            .Where(r => r.MaximumQuantity == null || r.MaximumQuantity >= quantity)
            .OrderByDescending(r => r.Priority)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PriceRule>> GetAllPagedAsync(Guid? productId, int page, int pageSize, CancellationToken ct = default)
    {
        pageSize = Math.Clamp(pageSize, 1, 100);
        page = Math.Max(1, page);

        var query = _context.PriceRules.Include(r => r.TieredPrices).AsQueryable();
        if (productId.HasValue)
            query = query.Where(r => r.ProductId == productId.Value);

        return await query
            .OrderByDescending(r => r.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task AddAsync(PriceRule rule, CancellationToken ct = default)
    {
        await _context.PriceRules.AddAsync(rule, ct).ConfigureAwait(false);
    }

    public Task SaveChangesAsync(CancellationToken ct = default)
    {
        return _context.SaveChangesAsync(ct);
    }
}
