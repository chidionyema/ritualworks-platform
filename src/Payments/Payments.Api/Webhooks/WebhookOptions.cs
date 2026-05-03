using System.ComponentModel.DataAnnotations;

namespace Haworks.Payments.Api.Webhooks;

/// <summary>
/// Configuration options for inbound webhook endpoints. Bound from
/// configuration section <c>Webhooks</c>; for production these values
/// are pulled from Vault (see Phase 3+ Vault wiring).
/// </summary>
public sealed class WebhookOptions
{
    public const string SectionName = "Webhooks";

    public StripeWebhookOptions Stripe { get; set; } = new();
    public PayPalWebhookOptions PayPal { get; set; } = new();
}

public sealed class StripeWebhookOptions
{
    /// <summary>
    /// Stripe webhook signing secret (whsec_...). Production value lives in
    /// Vault at <c>secret/payments/stripe.WebhookSecret</c>; tests + dev
    /// can supply via configuration directly.
    /// </summary>
    [Required]
    public string WebhookSecret { get; set; } = string.Empty;
}

public sealed class PayPalWebhookOptions
{
    /// <summary>
    /// PayPal webhook ID issued at PayPal-side webhook configuration time.
    /// Required for the verify-signature API call.
    /// </summary>
    public string WebhookId { get; set; } = string.Empty;
}
