using Haworks.Pricing.Application.Interfaces;
using Haworks.Pricing.Domain.Exceptions;
using Microsoft.Extensions.Logging;

namespace Haworks.Pricing.Infrastructure.Adapters;

/// <summary>
/// Database-backed tax calculator. Looks up TaxRate from PricingDbContext.
/// Fail-closed: throws TaxCalculationException if rate cannot be determined for non-null jurisdiction.
/// </summary>
public sealed class RateTableTaxCalculator : ITaxCalculator
{
    private readonly ITaxRateRepository _repository;
    private readonly ILogger<RateTableTaxCalculator> _logger;

    public RateTableTaxCalculator(ITaxRateRepository repository, ILogger<RateTableTaxCalculator> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task<TaxCalculationResult> CalculateAsync(
        string? countryCode,
        string? stateCode,
        decimal subtotal,
        string currency,
        CancellationToken ct = default)
    {
        // Null jurisdiction => no tax
        if (string.IsNullOrWhiteSpace(countryCode))
        {
            return new TaxCalculationResult(0m, 0m, "None");
        }

        var now = DateTimeOffset.UtcNow;
        var rate = await _repository.GetRateAsync(countryCode, stateCode, now, ct).ConfigureAwait(false);

        if (rate is null)
        {
            _logger.LogError("No tax rate configured for {Country}/{State}", countryCode, stateCode);
            throw new TaxCalculationException($"No tax rate configured for {countryCode}/{stateCode}");
        }

        var taxAmount = Math.Round(rate.CombinedRate * subtotal, 4, MidpointRounding.AwayFromZero);
        return new TaxCalculationResult(taxAmount, rate.CombinedRate, "RateTable");
    }
}
