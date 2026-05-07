using Haworks.Payments.Domain;

namespace Haworks.Payments.Infrastructure.Options;

/// <summary>
/// Configuration options for payment providers.
/// Bound from "PaymentProviders" section.
/// </summary>
public sealed class PaymentProviderOptions
{
    public const string SectionName = "PaymentProviders";

    /// <summary>
    /// The active payment provider to use.
    /// </summary>
    public PaymentProvider Active { get; set; } = PaymentProvider.Stripe;

    /// <summary>
    /// Stripe-specific configuration.
    /// </summary>
    public StripeOptions Stripe { get; set; } = new();
}
