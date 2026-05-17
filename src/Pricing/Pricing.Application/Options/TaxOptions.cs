namespace Haworks.Pricing.Application.Options;

/// <summary>
/// Configuration options for the tax calculation module.
/// </summary>
public sealed class TaxOptions
{
    public const string SectionName = "Tax";

    /// <summary>
    /// When true, throws TaxCalculationException if no rate is found.
    /// When false (default), returns 0% and logs a warning.
    /// </summary>
    public bool FailOpen { get; set; }

    /// <summary>
    /// Configurable rate table entries.
    /// </summary>
    public List<TaxRateEntry> Rates { get; set; } = new();
}

/// <summary>
/// A single tax rate entry in the configuration.
/// </summary>
public sealed class TaxRateEntry
{
    public string Country { get; set; } = "*";
    public string? State { get; set; }
    public decimal Rate { get; set; }
}
