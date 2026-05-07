using Haworks.BuildingBlocks.Resilience;
using Haworks.Payments.Application.Interfaces;
using Haworks.Payments.Domain;
using Microsoft.Extensions.Logging;
using Polly;
using Stripe;
using Stripe.Checkout;

namespace Haworks.Payments.Infrastructure.Stripe;

/// <summary>
/// Stripe implementation of ICheckoutSessionService.
/// Handles one-time payment checkout sessions.
/// </summary>
internal sealed class StripeCheckoutSessionService(
    IStripeClientFactory clientFactory,
    IResiliencePolicyFactory resiliencePolicyFactory,
    ILogger<StripeCheckoutSessionService> logger) : ICheckoutSessionService
{
    private readonly IAsyncPolicy _resiliencePolicy =
        resiliencePolicyFactory.CreateCombinedPolicy(ResilienceOptions.Stripe);

    /// <inheritdoc />
    public async Task<CheckoutSessionResult> CreateSessionAsync(
        CreateCheckoutSessionRequest request,
        CancellationToken ct = default)
    {
        var sessionService = await GetSessionServiceAsync(ct);
        var options = BuildPaymentSessionOptions(request);
        var requestOptions = new RequestOptions { IdempotencyKey = request.IdempotencyKey };

        return await _resiliencePolicy.ExecuteAsync(async (ctx, token) =>
        {
            logger.LogInformation(
                "Creating Stripe payment session with idempotency key {Key}",
                request.IdempotencyKey);

            var session = await sessionService.CreateAsync(options, requestOptions, token);

            logger.LogInformation("Stripe payment session created: {SessionId}", session.Id);

            return new CheckoutSessionResult
            {
                SessionId = session.Id,
                SessionUrl = session.Url,
                TransactionId = session.PaymentIntentId,
                Provider = PaymentProvider.Stripe
            };
        }, new Context(), ct);
    }

    /// <inheritdoc />
    public async Task<CheckoutSession?> GetSessionAsync(
        string sessionId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            logger.LogWarning("GetSessionAsync called with empty sessionId");
            return null;
        }

        var sessionService = await GetSessionServiceAsync(ct);

        return await _resiliencePolicy.ExecuteAsync(async (ctx, token) =>
        {
            try
            {
                var session = await sessionService.GetAsync(sessionId, cancellationToken: token);
                return MapToCheckoutSession(session);
            }
            catch (StripeException ex) when (ex.StripeError?.Code == StripeConstants.ErrorCodes.ResourceMissing)
            {
                logger.LogWarning("Stripe session {SessionId} not found", sessionId);
                return null;
            }
        }, new Context(), ct);
    }

    /// <inheritdoc />
    public async Task<bool> ExpireSessionAsync(
        string sessionId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            logger.LogWarning("ExpireSessionAsync called with empty sessionId");
            return false;
        }

        var sessionService = await GetSessionServiceAsync(ct);

        return await _resiliencePolicy.ExecuteAsync(async (ctx, token) =>
        {
            try
            {
                logger.LogInformation("Expiring Stripe session {SessionId}", sessionId);
                await sessionService.ExpireAsync(sessionId, cancellationToken: token);
                logger.LogInformation("Stripe session {SessionId} expired successfully", sessionId);
                return true;
            }
            catch (StripeException ex) when (ex.StripeError?.Code == StripeConstants.ErrorCodes.ResourceMissing)
            {
                logger.LogWarning("Cannot expire Stripe session {SessionId} - not found", sessionId);
                return false;
            }
            catch (StripeException ex) when (ex.Message.Contains("already expired"))
            {
                logger.LogInformation("Stripe session {SessionId} was already expired", sessionId);
                return true;
            }
        }, new Context(), ct);
    }

    private async Task<SessionService> GetSessionServiceAsync(CancellationToken ct)
    {
        var client = await clientFactory.GetClientAsync(ct);
        return new SessionService(client);
    }

    private static SessionCreateOptions BuildPaymentSessionOptions(CreateCheckoutSessionRequest request)
    {
        var lineItems = request.LineItems.Select(item => new SessionLineItemOptions
        {
            PriceData = new SessionLineItemPriceDataOptions
            {
                Currency = item.Currency,
                ProductData = new SessionLineItemPriceDataProductDataOptions
                {
                    Name = item.Name,
                    Description = item.Description
                },
                UnitAmount = item.UnitAmountCents
            },
            Quantity = item.Quantity
        }).ToList();

        var options = new SessionCreateOptions
        {
            PaymentMethodTypes = [StripeConstants.PaymentMethods.Card],
            LineItems = lineItems,
            Mode = StripeConstants.SessionModes.Payment,
            SuccessUrl = request.SuccessUrl,
            CancelUrl = request.CancelUrl,
            CustomerEmail = string.IsNullOrEmpty(request.CustomerEmail) ? null : request.CustomerEmail,
            Customer = request.CustomerId,
            Metadata = request.Metadata
        };

        // Add orderId to metadata if present to ensure validation works
        if (request.OrderId.HasValue && !options.Metadata.ContainsKey("orderId"))
        {
            options.Metadata["orderId"] = request.OrderId.Value.ToString();
        }

        return options;
    }

    private static CheckoutSession MapToCheckoutSession(Session session)
    {
        return new CheckoutSession
        {
            SessionId = session.Id,
            Status = MapSessionStatus(session.Status),
            TransactionId = session.PaymentIntentId ?? session.SubscriptionId,
            CustomerId = session.CustomerId,
            AmountTotal = session.AmountTotal,
            Currency = session.Currency,
            Provider = PaymentProvider.Stripe,
            Metadata = session.Metadata?.ToDictionary(k => k.Key, v => v.Value)
                       ?? []
        };
    }

    private static SessionStatus MapSessionStatus(string? stripeStatus)
    {
        return stripeStatus switch
        {
            StripeConstants.SessionStatuses.Open => SessionStatus.Open,
            StripeConstants.SessionStatuses.Complete => SessionStatus.Complete,
            StripeConstants.SessionStatuses.Expired => SessionStatus.Expired,
            _ => SessionStatus.Open
        };
    }
}
