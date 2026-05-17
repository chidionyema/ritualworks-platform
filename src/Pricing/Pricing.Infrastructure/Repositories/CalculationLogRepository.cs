using Haworks.Pricing.Application.Interfaces;
using Haworks.Pricing.Domain.Entities;
using Haworks.Pricing.Infrastructure.Persistence;

namespace Haworks.Pricing.Infrastructure.Repositories;

/// <summary>
/// Append-only repository for PriceCalculationLog.
/// </summary>
public sealed class CalculationLogRepository : ICalculationLogRepository
{
    private readonly PricingDbContext _context;

    public CalculationLogRepository(PricingDbContext context)
    {
        _context = context;
    }

    public async Task AddAsync(PriceCalculationLog log, CancellationToken ct = default)
    {
        await _context.CalculationLogs.AddAsync(log, ct).ConfigureAwait(false);
    }

    public Task SaveChangesAsync(CancellationToken ct = default)
    {
        return _context.SaveChangesAsync(ct);
    }
}
