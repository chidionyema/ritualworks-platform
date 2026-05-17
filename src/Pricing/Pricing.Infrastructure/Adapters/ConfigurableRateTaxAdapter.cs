using Haworks.Pricing.Application.Interfaces;
using Haworks.Pricing.Application.Options;
using Haworks.Pricing.Domain.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Haworks.Pricing.Infrastructure.Adapters;

/// <summary>
/// v1 tax calculator using configurable in-process rate table.
/// Fail-closed by default: unknown jurisdiction returns 500, not silent zero.
/// </summary>
public sealed class ConfigurableRateTaxAdapter : ITaxCalculator
{
    private readonly TaxOptions _options;
    private readonly ILogger<ConfigurableRateTaxAdapter> _logger;

    public ConfigurableRateTaxAdapter(IOptions<TaxOptions> options, ILogger<ConfigurableRateTaxAdapter> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public Task<TaxCalculationResult> CalculateAsync(
        string? countryCode,
        string? stateCode,
        decimal subtotal,
        string currency,
        CancellationToken ct = default)
    {
        // Null jurisdiction => no tax
        if (string.IsNullOrWhiteSpace(countryCode))
        {
            return Task.FromResult(new TaxCalculationResult(0m, 0m, "None"));
        }

        var rate = ResolveRate(countryCode, stateCode);

        if (rate is null)
        {
            if (_options.FailOpen)
            {
                throw new TaxCalculationException($"No tax rate configured for {countryCode}/{stateCode}");
            }

            _logger.LogWarning(
                "No tax rate configured for {Country}/{State}. Returning 0% (FailOpen=false).",
                countryCode, stateCode);
            return Task.FromResult(new TaxCalculationResult(0m, 0m, "RateTable"));
        }

        var taxAmount = Math.Round(subtotal * rate.Value, 4, MidpointRounding.AwayFromZero);
        return Task.FromResult(new TaxCalculationResult(taxAmount, rate.Value, "RateTable"));
    }

    private decimal? ResolveRate(string countryCode, string? stateCode)
    {
        var country = countryCode.ToUpperInvariant();
        var state = stateCode?.ToUpperInvariant();

        // 1. Exact country + state match
        var entry = _options.Rates.FirstOrDefault(r =>
            string.Equals(r.Country, country, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(r.State, state, StringComparison.OrdinalIgnoreCase));

        if (entry is not null) return entry.Rate;

        // 2. Country + state=null fallback
        entry = _options.Rates.FirstOrDefault(r =>
            string.Equals(r.Country, country, StringComparison.OrdinalIgnoreCase) &&
            r.State is null);

        if (entry is not null) return entry.Rate;

        // 3. Wildcard country
        entry = _options.Rates.FirstOrDefault(r =>
            string.Equals(r.Country, "*", StringComparison.OrdinalIgnoreCase) &&
            r.State is null);

        return entry?.Rate;
    }
}
