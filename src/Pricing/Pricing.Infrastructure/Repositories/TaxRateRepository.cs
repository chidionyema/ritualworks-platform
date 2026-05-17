using Haworks.Pricing.Application.Interfaces;
using Haworks.Pricing.Domain.Entities;
using Haworks.Pricing.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Haworks.Pricing.Infrastructure.Repositories;

/// <summary>
/// EF Core implementation of ITaxRateRepository.
/// </summary>
public sealed class TaxRateRepository : ITaxRateRepository
{
    private readonly PricingDbContext _context;

    public TaxRateRepository(PricingDbContext context)
    {
        _context = context;
    }

    public async Task<TaxRate?> GetRateAsync(string countryCode, string? stateCode, DateTimeOffset now, CancellationToken ct = default)
    {
        var country = countryCode.ToUpperInvariant();
        var state = stateCode?.ToUpperInvariant();

        // Most specific first: country + state
        var rate = await _context.TaxRates
            .Where(r => r.CountryCode == country && r.StateCode == state)
            .Where(r => r.EffectiveFrom <= now)
            .Where(r => r.EffectiveTo == null || r.EffectiveTo > now)
            .OrderByDescending(r => r.EffectiveFrom)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (rate is not null) return rate;

        // Fallback: country only (state = null)
        return await _context.TaxRates
            .Where(r => r.CountryCode == country && r.StateCode == null)
            .Where(r => r.EffectiveFrom <= now)
            .Where(r => r.EffectiveTo == null || r.EffectiveTo > now)
            .OrderByDescending(r => r.EffectiveFrom)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TaxRate>> GetAllAsync(CancellationToken ct = default)
    {
        return await _context.TaxRates
            .OrderBy(r => r.CountryCode)
            .ThenBy(r => r.StateCode)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<TaxRate?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await _context.TaxRates.FindAsync(new object[] { id }, ct).ConfigureAwait(false);
    }

    public async Task AddAsync(TaxRate rate, CancellationToken ct = default)
    {
        await _context.TaxRates.AddAsync(rate, ct).ConfigureAwait(false);
    }

    public Task SaveChangesAsync(CancellationToken ct = default)
    {
        return _context.SaveChangesAsync(ct);
    }
}
