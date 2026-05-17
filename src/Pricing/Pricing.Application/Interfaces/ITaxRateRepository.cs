using Haworks.Pricing.Domain.Entities;

namespace Haworks.Pricing.Application.Interfaces;

/// <summary>
/// Repository for TaxRate lookups.
/// </summary>
public interface ITaxRateRepository
{
    Task<TaxRate?> GetRateAsync(string countryCode, string? stateCode, DateTimeOffset now, CancellationToken ct = default);
    Task<IReadOnlyList<TaxRate>> GetAllAsync(CancellationToken ct = default);
    Task<TaxRate?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task AddAsync(TaxRate rate, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
