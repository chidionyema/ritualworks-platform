using Haworks.Payments.Application.Interfaces;
using Haworks.Contracts.Payments;
using Haworks.Payments.Infrastructure.Options;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Stripe;

namespace Haworks.Payments.Infrastructure;

/// <summary>
/// Main facade for payment operations.
/// Provides a single entry point for all payment-related functionality,
/// delegating to the configured provider's implementations.
/// </summary>
internal sealed class PaymentGateway(
    IServiceProvider serviceProvider,
    IOptions<PaymentProviderOptions> options,
    ILogger<PaymentGateway> logger) : IPaymentGateway
{
    public PaymentProvider ActiveProvider { get; } = options.Value.Active;

    public ICheckoutSessionService Checkout => ActiveProvider switch
    {
        PaymentProvider.Stripe => serviceProvider.GetRequiredService<Stripe.StripeCheckoutSessionService>(),
        PaymentProvider.PayPal => serviceProvider.GetRequiredService<PayPal.PayPalCheckoutService>(),
        _ => throw new NotSupportedException($"Checkout not supported for {ActiveProvider}")
    };

    public ISubscriptionManager Subscriptions => ActiveProvider switch
    {
        PaymentProvider.Stripe => serviceProvider.GetRequiredService<Stripe.StripeSubscriptionManager>(),
        PaymentProvider.PayPal => serviceProvider.GetRequiredService<PayPal.PayPalSubscriptionManager>(),
        _ => throw new NotSupportedException($"Subscriptions not supported for {ActiveProvider}")
    };

    public IRefundService Refunds => ActiveProvider switch
    {
        PaymentProvider.Stripe => serviceProvider.GetRequiredService<Stripe.StripeRefundService>(),
        PaymentProvider.PayPal => serviceProvider.GetRequiredService<PayPal.PayPalRefundService>(),
        _ => throw new NotSupportedException($"Refunds not supported for {ActiveProvider}")
    };

    public IWebhookProcessor Webhooks => serviceProvider.GetRequiredService<Webhooks.WebhookRouter>().GetProcessor(ActiveProvider)
        ?? throw new InvalidOperationException($"No webhook processor found for {ActiveProvider}");

    public async Task<ProviderHealthStatus> CheckHealthAsync(CancellationToken ct = default)
    {
        logger.LogDebug("Checking health of payment provider {Provider}", ActiveProvider);

        var isHealthy = await PerformHealthCheckAsync(ct);

        return new ProviderHealthStatus
        {
            IsHealthy = isHealthy,
            Provider = ActiveProvider,
            Message = isHealthy ? "Provider is responsive" : "Provider health check failed"
        };
    }

    private async Task<bool> PerformHealthCheckAsync(CancellationToken ct)
    {
        try
        {
            return ActiveProvider switch
            {
                PaymentProvider.Stripe => await CheckStripeConnectivityAsync(ct),
                PaymentProvider.PayPal => await CheckPayPalConnectivityAsync(ct),
                _ => true
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Provider health check failed for {Provider}", ActiveProvider);
            return false;
        }
    }

    private async Task<bool> CheckStripeConnectivityAsync(CancellationToken ct)
    {
        try
        {
            // Lightweight call to verify connectivity and credentials
            await Checkout.GetSessionAsync("cs_health_check", ct);
            return true;
        }
        catch (StripeException ex) when (ex.StripeError?.Code == "resource_missing")
        {
            // Expected for non-existent session, confirms API is reachable
            return true;
        }
        catch (StripeException ex)
        {
            logger.LogError(ex, "Stripe connectivity check failed: {Message}", ex.Message);
            return false;
        }
    }

    private async Task<bool> CheckPayPalConnectivityAsync(CancellationToken ct)
    {
        try
        {
            await Checkout.GetSessionAsync("HEALTH_CHECK", ct);
            return true;
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("401") || ex.Message.Contains("403"))
        {
            logger.LogError("PayPal API credentials are invalid");
            return false;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "PayPal connectivity check failed");
            return false;
        }
    }
}
