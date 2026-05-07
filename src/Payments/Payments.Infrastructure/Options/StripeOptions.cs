using System.ComponentModel.DataAnnotations;

namespace Haworks.Payments.Infrastructure.Options;

/// <summary>
/// Stripe provider configuration.
/// </summary>
public sealed class StripeOptions
{
    public const string SectionName = "Stripe";

    /// <summary>
    /// Stripe secret API key (sk_...).
    /// </summary>
    [Required]
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>
    /// Stripe publishable API key (pk_...).
    /// </summary>
    [Required]
    public string PublishableKey { get; set; } = string.Empty;

    /// <summary>
    /// Webhook endpoint secret for signature validation (whsec_...).
    /// </summary>
    [Required]
    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>
    /// Secret for signing/validating metadata in checkout sessions.
    /// </summary>
    public string MetadataSignatureSecret { get; set; } = string.Empty;

    /// <summary>
    /// Optional base URL override for Stripe API (used for hermetic testing).
    /// </summary>
    [Url]
    public string? BaseUrl { get; set; }
}
