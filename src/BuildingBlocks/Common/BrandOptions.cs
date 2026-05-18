namespace Haworks.BuildingBlocks.Common;

/// <summary>
/// Strongly typed configuration for platform-wide white-labeling.
/// Allows dynamic rebranding via configuration providers (e.g. Vault).
/// </summary>
public sealed class BrandOptions
{
    public const string SectionName = "Brand";

    /// <summary>
    /// The external brand name (e.g. "Haworks").
    /// Used in email templates, payment checkout pages, and UI.
    /// </summary>
    public string Name { get; set; } = "Haworks";

    /// <summary>
    /// The primary support email for the brand.
    /// </summary>
    public string SupportEmail { get; set; } = "support@haworks.local";

    /// <summary>
    /// The root URL of the brand's primary portal.
    /// </summary>
    public string PrimaryUrl { get; set; } = "https://haworks.com";

    /// <summary>
    /// URL to the brand's logo.
    /// </summary>
    public string LogoUrl { get; set; } = string.Empty;

    /// <summary>
    /// Company registration / legal name if different from brand name.
    /// </summary>
    public string LegalName { get; set; } = "Haworks Ltd.";

    /// <summary>
    /// The default currency code for the brand (ISO 4217).
    /// Used as fallback for pricing and payments.
    /// </summary>
    public string DefaultCurrency { get; set; } = "USD";
}
