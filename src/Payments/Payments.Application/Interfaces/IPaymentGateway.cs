using Haworks.Payments.Domain;

namespace Haworks.Payments.Application.Interfaces;

/// <summary>
/// Main facade for payment operations. Provides a single entry point
/// for all payment-related functionality, delegating to the configured provider.
/// </summary>
public interface IPaymentGateway
{
    /// <summary>
    /// The currently active payment provider.
    /// </summary>
    PaymentProvider ActiveProvider { get; }

    /// <summary>
    /// Checkout session operations (create, retrieve, expire).
    /// </summary>
    ICheckoutSessionService Checkout { get; }

    /// <summary>
    /// Webhook validation and processing.
    /// </summary>
    IWebhookProcessor Webhooks { get; }
}
