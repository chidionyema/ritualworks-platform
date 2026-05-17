namespace Haworks.Pricing.Application.Interfaces;

/// <summary>
/// Calculates tax for a given jurisdiction and subtotal.
/// v1: in-process rate table. v2: Avalara/TaxJar adapter.
/// Never returns a silent zero for a known taxable jurisdiction.
/// Throws TaxCalculationException (mapped to HTTP 500) if rate cannot be determined.
/// </summary>
public interface ITaxCalculator
{
    Task<TaxCalculationResult> CalculateAsync(
        string? countryCode,
        string? stateCode,
        decimal subtotal,
        string currency,
        CancellationToken ct = default);
}

/// <summary>
/// Result of a tax calculation.
/// </summary>
public sealed record TaxCalculationResult(
    decimal TaxAmount,
    decimal EffectiveRate,
    string Source);
