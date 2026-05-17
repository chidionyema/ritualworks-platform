using Haworks.Payments.Application.Interfaces;
using Haworks.BuildingBlocks.Resilience;
using Haworks.Contracts.Payments;
using Microsoft.Extensions.Logging;
using Polly;
using Stripe;
using Stripe.Checkout;

namespace Haworks.Payments.Infrastructure.Stripe;

internal sealed class StripeSubscriptionService(
    IStripeClientFactory clientFactory,
    IResiliencePolicyFactory resiliencePolicyFactory,
    ILogger<StripeSubscriptionService> logger) : ISubscriptionService
{
    private readonly IAsyncPolicy _resiliencePolicy = resiliencePolicyFactory.CreateCombinedPolicy(ResilienceOptions.Stripe);

    public Task<CheckoutSessionResult> CreateSubscriptionSessionAsync(
        CreateSubscriptionSessionRequest request,
        CancellationToken ct = default)
    {
        logger.LogInformation("Creating subscription checkout session for plan {PlanId}, user {UserId}",
            request.PlanId, request.UserId);

        return _resiliencePolicy.ExecuteAsync(async (ctx, token) =>
        {
            var client = await clientFactory.GetClientAsync(token);
            var service = new SessionService(client);

            var options = new SessionCreateOptions
            {
                PaymentMethodTypes = new List<string> { StripeConstants.PaymentMethods.Card },
                LineItems = new List<SessionLineItemOptions>
                {
                    new SessionLineItemOptions { Price = request.PlanId, Quantity = 1 }
                },
                Mode = StripeConstants.SessionModes.Subscription,
                SuccessUrl = request.SuccessUrl,
                CancelUrl = request.CancelUrl,
                CustomerEmail = request.CustomerEmail,
                Metadata = request.Metadata != null ? new Dictionary<string, string>(request.Metadata) : new Dictionary<string, string>()
            };

            if (!options.Metadata.ContainsKey("user_id")) options.Metadata["user_id"] = request.UserId;

            var requestOptions = new RequestOptions { IdempotencyKey = request.IdempotencyKey };
            Session session;
            try
            {
                session = await service.CreateAsync(options, requestOptions, token);
            }
            catch (StripeException ex)
            {
                logger.LogError(ex, "Stripe subscription session creation failed for plan {PlanId}, user {UserId}",
                    request.PlanId, request.UserId);
                throw;
            }

            logger.LogInformation("Subscription session {SessionId} created for plan {PlanId}",
                session.Id, request.PlanId);

            return new CheckoutSessionResult
            {
                SessionId = session.Id,
                SessionUrl = session.Url,
                Provider = PaymentProvider.Stripe
            };
        }, new Context(), ct);
    }
}
