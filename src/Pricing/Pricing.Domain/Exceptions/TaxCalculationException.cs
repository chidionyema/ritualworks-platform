namespace Haworks.Pricing.Domain.Exceptions;

/// <summary>
/// Thrown when tax cannot be calculated for a given jurisdiction.
/// Maps to HTTP 500 — fail-closed policy.
/// </summary>
public sealed class TaxCalculationException : Exception
{
    public TaxCalculationException(string message) : base(message) { }
    public TaxCalculationException(string message, Exception inner) : base(message, inner) { }
}
