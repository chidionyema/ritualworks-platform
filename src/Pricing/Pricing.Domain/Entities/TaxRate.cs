using Haworks.BuildingBlocks.Persistence;

namespace Haworks.Pricing.Domain.Entities;

/// <summary>
/// Configurable tax rate for a country/state jurisdiction.
/// </summary>
public sealed class TaxRate : AuditableEntity
{
    private TaxRate() { }

    public string CountryCode { get; private set; } = string.Empty;
    public string? StateCode { get; private set; }
    public decimal CombinedRate { get; private set; }
    public decimal StateRate { get; private set; }
    public decimal CountyRate { get; private set; }
    public decimal LocalRate { get; private set; }
    public DateTimeOffset EffectiveFrom { get; private set; }
    public DateTimeOffset? EffectiveTo { get; private set; }
    public string? Notes { get; private set; }

    public static TaxRate Create(
        string countryCode,
        string? stateCode,
        decimal combinedRate,
        decimal stateRate = 0,
        decimal countyRate = 0,
        decimal localRate = 0,
        DateTimeOffset? effectiveFrom = null,
        DateTimeOffset? effectiveTo = null,
        string? notes = null)
    {
        return new TaxRate
        {
            CountryCode = countryCode.ToUpperInvariant(),
            StateCode = stateCode?.ToUpperInvariant(),
            CombinedRate = combinedRate,
            StateRate = stateRate,
            CountyRate = countyRate,
            LocalRate = localRate,
            EffectiveFrom = effectiveFrom ?? DateTimeOffset.UtcNow,
            EffectiveTo = effectiveTo,
            Notes = notes,
        };
    }
}
