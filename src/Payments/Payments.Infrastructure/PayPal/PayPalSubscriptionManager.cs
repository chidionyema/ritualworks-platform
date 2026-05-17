using Haworks.Payments.Application.Interfaces;
using Haworks.Payments.Domain.Interfaces;
using Haworks.BuildingBlocks.Telemetry;
using Haworks.BuildingBlocks.Resilience;
using Haworks.Contracts.Payments;
using Haworks.Payments.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Polly;
using System.Net.Http.Json;
using System.Text.Json;
using DomainSubscription = Haworks.Payments.Domain.Subscription;
using DomainSubscriptionStatus = Haworks.Payments.Domain.SubscriptionStatus;

namespace Haworks.Payments.Infrastructure.PayPal;

/// <summary>
/// PayPal implementation of ISubscriptionManager.
/// Handles subscription lifecycle operations using the PayPal Billing Subscriptions API.
/// </summary>
internal sealed class PayPalSubscriptionManager(
    IPaymentRepository paymentRepository,
    IDomainEventPublisher eventPublisher,
    IPayPalClientFactory clientFactory,
    IResiliencePolicyFactory resiliencePolicyFactory,
    ILogger<PayPalSubscriptionManager> logger,
    ITelemetryService telemetry) : ISubscriptionManager
{
    private readonly IAsyncPolicy _resiliencePolicy = 
        resiliencePolicyFactory.CreateCombinedPolicy(ResilienceOptions.PayPal);

    /// <inheritdoc />
    public async Task<SubscriptionStatusResult> GetStatusAsync(string userId, CancellationToken ct = default)
    {
        var subscription = await paymentRepository.GetSubscriptionByUserIdAsync(userId, ct);
        if (subscription == null || subscription.Provider != PaymentProvider.PayPal)
        {
            return new SubscriptionStatusResult { IsActive = false, Provider = PaymentProvider.PayPal };
        }

        // Let transient exceptions propagate to Polly for retry.
        try
        {
            return await _resiliencePolicy.ExecuteAsync(async (ctx, token) =>
            {
                var client = await clientFactory.GetAuthenticatedClientAsync(token);
                var response = await client.GetAsync(PayPalEndpoints.GetSubscription(subscription.ProviderSubscriptionId), token);

                if (!response.IsSuccessStatusCode)
                {
                    var code = (int)response.StatusCode;
                    if (code >= 400 && code < 500 && code != 429)
                    {
                        // Non-transient client error: return local data without retry
                        logger.LogWarning("Non-transient error fetching PayPal subscription {SubscriptionId}: {StatusCode}",
                            subscription.ProviderSubscriptionId, response.StatusCode);
                        return MapToStatusResult(subscription);
                    }

                    // Transient server error: throw so Polly retries
                    response.EnsureSuccessStatusCode();
                }

                var paypalSub = await response.Content.ReadFromJsonAsync<PayPalSubscriptionResponse>(PayPalJsonOptions.Default, token);
                var newStatus = MapSubscriptionStatus(paypalSub?.Status);

                // Update local record if status changed
                if (subscription.Status != newStatus)
                {
                    subscription.UpdateStatus(newStatus);
                    await paymentRepository.SaveChangesAsync(token);
                }

                return new SubscriptionStatusResult
                {
                    IsActive = subscription.IsActive,
                    SubscriptionId = subscription.ProviderSubscriptionId,
                    PlanId = subscription.PlanId,
                    Status = subscription.Status,
                    CurrentPeriodEnd = subscription.ExpiresAt,
                    CanceledAt = subscription.CanceledAt,
                    Provider = PaymentProvider.PayPal
                };
            }, new Context(), ct);
        }
        catch (HttpRequestException ex)
        {
            // Polly exhausted retries for transient errors — fall back to local data
            logger.LogError(ex, "Error verifying PayPal subscription {SubscriptionId} after retries exhausted",
                subscription.ProviderSubscriptionId);
            return MapToStatusResult(subscription);
        }
    }

    /// <inheritdoc />
    public Task<bool> CancelAsync(string subscriptionId, bool immediate = false, CancellationToken ct = default)
    {
        return _resiliencePolicy.ExecuteAsync(async (ctx, token) =>
        {
            var client = await clientFactory.GetAuthenticatedClientAsync(token);
            
            // PayPal doesn't have a direct "cancel at period end" update like Stripe.
            // We usually cancel and let the local record manage access until expiry.
            var cancelRequest = new PayPalCancelSubscriptionRequest { Reason = "User requested cancellation" };
            var response = await client.PostAsJsonAsync(PayPalEndpoints.CancelSubscription(subscriptionId), cancelRequest, token);
            
            if (response.IsSuccessStatusCode)
            {
                logger.LogInformation("PayPal subscription {SubscriptionId} cancellation requested", subscriptionId);
                
                // Note: Domain events and DB updates are typically handled by webhooks (BILLING.SUBSCRIPTION.CANCELLED)
                // for consistency across all async flows.
                
                telemetry.TrackEvent("SubscriptionCancellationRequested", new Dictionary<string, string>
                {
                    ["Provider"] = PaymentProvider.PayPal.ToString(),
                    ["SubscriptionId"] = subscriptionId
                });

                return true;
            }

            var errorBody = await response.Content.ReadAsStringAsync(token);
            logger.LogError("PayPal subscription cancellation failed: {Body}", errorBody);
            return false;
        }, new Context(), ct);
    }

    /// <inheritdoc />
    public Task<bool> ResumeAsync(string subscriptionId, CancellationToken ct = default)
    {
        return _resiliencePolicy.ExecuteAsync(async (ctx, token) =>
        {
            var client = await clientFactory.GetAuthenticatedClientAsync(token);
            
            // Activate previously suspended subscription
            var response = await client.PostAsJsonAsync(PayPalEndpoints.ActivateSubscription(subscriptionId), new { reason = "User resumed" }, token);
            
            if (response.IsSuccessStatusCode)
            {
                logger.LogInformation("PayPal subscription {SubscriptionId} activation requested", subscriptionId);
                return true;
            }

            return false;
        }, new Context(), ct);
    }

    /// <inheritdoc />
    public async Task HandleSubscriptionEventAsync(SubscriptionEvent subscriptionEvent, CancellationToken ct = default)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["SubscriptionId"] = subscriptionEvent.SubscriptionId,
            ["EventType"] = subscriptionEvent.EventType.ToString(),
            ["Provider"] = PaymentProvider.PayPal.ToString()
        });

        var existing = await paymentRepository.GetSubscriptionByProviderIdAsync(subscriptionEvent.SubscriptionId, ct);

        switch (subscriptionEvent.EventType)
        {
            case SubscriptionEventType.Created:
                await HandleCreatedAsync(subscriptionEvent, existing, ct);
                break;

            case SubscriptionEventType.Updated:
            case SubscriptionEventType.Resumed:
                await HandleUpdatedOrResumedAsync(subscriptionEvent, existing, ct);
                break;

            case SubscriptionEventType.Renewed:
                await HandleRenewedAsync(subscriptionEvent, existing, ct);
                break;

            case SubscriptionEventType.Canceled:
            case SubscriptionEventType.Expired:
                await HandleCanceledAsync(subscriptionEvent, existing, ct);
                break;
        }
    }

    private async Task HandleCreatedAsync(SubscriptionEvent subscriptionEvent, DomainSubscription? existing, CancellationToken ct)
    {
        if (existing != null) return;

        var newSub = DomainSubscription.Create(
            subscriptionEvent.UserId!,
            PaymentProvider.PayPal,
            subscriptionEvent.SubscriptionId,
            subscriptionEvent.PlanId!,
            DateTime.UtcNow,
            subscriptionEvent.CurrentPeriodEnd ?? DateTime.UtcNow.AddMonths(1));

        newSub.UpdateStatus(subscriptionEvent.NewStatus);
        await paymentRepository.AddSubscriptionAsync(newSub, ct);

        await eventPublisher.PublishAsync(new SubscriptionStartedEvent
        {
            SubscriptionId = newSub.ProviderSubscriptionId,
            UserId = newSub.UserId,
            PlanId = newSub.PlanId,
            Provider = PaymentProvider.PayPal,
            CurrentPeriodEnd = newSub.ExpiresAt
        }, ct);
        await paymentRepository.SaveChangesAsync(ct);
    }

    private async Task HandleUpdatedOrResumedAsync(SubscriptionEvent subscriptionEvent, DomainSubscription? existing, CancellationToken ct)
    {
        if (existing == null) return;

        existing.UpdateStatus(subscriptionEvent.NewStatus);
        if (subscriptionEvent.CurrentPeriodEnd.HasValue)
        {
            existing.SetExpiresAt(subscriptionEvent.CurrentPeriodEnd.Value);
        }
        if (subscriptionEvent.EventType == SubscriptionEventType.Resumed)
        {
            existing.Activate();
        }
        await paymentRepository.SaveChangesAsync(ct);
    }

    private async Task HandleRenewedAsync(SubscriptionEvent subscriptionEvent, DomainSubscription? existing, CancellationToken ct)
    {
        if (existing == null) return;

        existing.UpdateStatus(subscriptionEvent.NewStatus);
        if (subscriptionEvent.CurrentPeriodEnd.HasValue)
        {
            existing.SetExpiresAt(subscriptionEvent.CurrentPeriodEnd.Value);
        }

        _ = long.TryParse(subscriptionEvent.Metadata.GetValueOrDefault("amount_cents"), out var amount);
        var currency = subscriptionEvent.Metadata.GetValueOrDefault("currency", "USD");

        await eventPublisher.PublishAsync(new SubscriptionRenewedEvent
        {
            SubscriptionId = existing.ProviderSubscriptionId,
            UserId = existing.UserId,
            Provider = PaymentProvider.PayPal,
            AmountCents = amount,
            Currency = currency,
            NewPeriodEnd = existing.ExpiresAt
        }, ct);
        await paymentRepository.SaveChangesAsync(ct);
    }

    private async Task HandleCanceledAsync(SubscriptionEvent subscriptionEvent, DomainSubscription? existing, CancellationToken ct)
    {
        if (existing == null) return;

        existing.Cancel();
        if (subscriptionEvent.CurrentPeriodEnd.HasValue)
        {
            existing.SetExpiresAt(subscriptionEvent.CurrentPeriodEnd.Value);
        }

        await eventPublisher.PublishAsync(new SubscriptionCancelledEvent
        {
            SubscriptionId = existing.ProviderSubscriptionId,
            UserId = existing.UserId,
            Provider = PaymentProvider.PayPal,
            Reason = subscriptionEvent.Metadata.GetValueOrDefault("reason")
        }, ct);
        await paymentRepository.SaveChangesAsync(ct);
    }

    private static DomainSubscriptionStatus MapSubscriptionStatus(string? paypalStatus)
    {
        return paypalStatus?.ToUpperInvariant() switch
        {
            "ACTIVE" => DomainSubscriptionStatus.Active,
            "CANCELLED" => DomainSubscriptionStatus.Canceled,
            "SUSPENDED" => DomainSubscriptionStatus.PastDue, // PayPal suspended map to PastDue
            "EXPIRED" => DomainSubscriptionStatus.Expired,
            _ => DomainSubscriptionStatus.Unknown
        };
    }

    private static SubscriptionStatusResult MapToStatusResult(DomainSubscription subscription)
    {
        return new SubscriptionStatusResult
        {
            IsActive = subscription.IsActive,
            SubscriptionId = subscription.ProviderSubscriptionId,
            PlanId = subscription.PlanId,
            Status = subscription.Status,
            CurrentPeriodEnd = subscription.ExpiresAt,
            CanceledAt = subscription.CanceledAt,
            Provider = PaymentProvider.PayPal
        };
    }
}
