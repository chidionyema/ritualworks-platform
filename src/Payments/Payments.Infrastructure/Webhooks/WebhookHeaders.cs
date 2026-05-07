namespace Haworks.Payments.Infrastructure.Webhooks;

/// <summary>
/// Constants for webhook signature headers from various payment providers.
/// </summary>
internal static class WebhookHeaders
{
    /// <summary>Stripe webhook signature header.</summary>
    public const string Stripe = "Stripe-Signature";

    /// <summary>PayPal transmission signature header.</summary>
    public const string PayPal = "PayPal-Transmission-Sig";

    /// <summary>Square webhook signature header.</summary>
    public const string Square = "X-Square-Signature";

    /// <summary>Braintree webhook signature header.</summary>
    public const string Braintree = "Bt-Signature";

    /// <summary>PayPal transmission ID header.</summary>
    public const string PayPalTransmissionId = "PayPal-Transmission-Id";

    /// <summary>PayPal transmission time header.</summary>
    public const string PayPalTransmissionTime = "PayPal-Transmission-Time";

    /// <summary>PayPal authentication algorithm header.</summary>
    public const string PayPalAuthAlgo = "PayPal-Auth-Algo";

    /// <summary>PayPal certificate URL header.</summary>
    public const string PayPalCertUrl = "PayPal-Cert-Url";
}
