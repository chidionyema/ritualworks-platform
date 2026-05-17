using Haworks.Pricing.Domain.Entities;

namespace Haworks.Pricing.Application.Interfaces;

/// <summary>
/// Append-only repository for price calculation audit logs.
/// </summary>
public interface ICalculationLogRepository
{
    Task AddAsync(PriceCalculationLog log, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
