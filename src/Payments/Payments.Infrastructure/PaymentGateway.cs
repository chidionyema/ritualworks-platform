using Haworks.Payments.Application.Interfaces;
using Haworks.Payments.Domain;
using Haworks.Payments.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace Haworks.Payments.Infrastructure;

/// <summary>
/// Main facade for payment operations.
/// Provides a single entry point for all payment-related functionality,
/// delegating to the configured provider's implementations.
/// </summary>
internal sealed class PaymentGateway(
    IOptions<PaymentProviderOptions> options,
    ICheckoutSessionService checkout,
    IWebhookProcessor webhooks) : IPaymentGateway
{
    public PaymentProvider ActiveProvider { get; } = options.Value.Active;
    public ICheckoutSessionService Checkout { get; } = checkout;
    public IWebhookProcessor Webhooks { get; } = webhooks;
}
