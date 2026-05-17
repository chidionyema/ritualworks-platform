using Haworks.Payments.Application.Interfaces;
using Haworks.Payments.Domain.Interfaces;
using Haworks.BuildingBlocks.Telemetry;
using Haworks.BuildingBlocks.Resilience;
using Haworks.Contracts.Payments;
using Microsoft.Extensions.Logging;
using Polly;
using Stripe;
using NativeStripeSubService = Stripe.SubscriptionService;
using DomainSubscription = Haworks.Payments.Domain.Subscription;
using DomainSubscriptionStatus = Haworks.Payments.Domain.SubscriptionStatus;

namespace Haworks.Payments.Infrastructure.Stripe;

/// <summary>
/// Stripe implementation of ISubscriptionManager.
/// Handles subscription lifecycle operations using the Stripe API.
/// </summary>
public sealed class StripeSubscriptionManager(
    IPaymentRepository paymentRepository,
    IStripeClientFactory clientFactory,
    IDomainEventPublisher eventPublisher,
    IResiliencePolicyFactory resiliencePolicyFactory,
    ILogger<StripeSubscriptionManager> logger,
    ITelemetryService telemetry) : ISubscriptionManager
{
    private readonly IAsyncPolicy _resiliencePolicy = 
        resiliencePolicyFactory.CreateCombinedPolicy(ResilienceOptions.Stripe);

    /// <inheritdoc />
    public async Task<SubscriptionStatusResult> GetStatusAsync(string userId, CancellationToken ct = default)
    {
        var subscription = await paymentRepository.GetSubscriptionByUserIdAsync(userId, ct);
        if (subscription == null) 
        {
            return new SubscriptionStatusResult 
            { 
                IsActive = false, 
                Status = DomainSubscriptionStatus.Unknown, 
                Provider = PaymentProvider.Stripe 
            };
        }

        // For Stripe, we can verify with the API for real-time accuracy if needed,
        // but for now, we follow the platform pattern of relying on webhooks to keep DB in sync.
        return new SubscriptionStatusResult 
        { 
            IsActive = subscription.IsActive, 
            SubscriptionId = subscription.ProviderSubscriptionId, 
            PlanId = subscription.PlanId,
            Status = subscription.Status, 
            CurrentPeriodEnd = subscription.ExpiresAt,
            CanceledAt = subscription.CanceledAt,
            Provider = PaymentProvider.Stripe 
        };
    }

    /// <inheritdoc />
    public async Task<bool> CancelAsync(string subscriptionId, bool immediate = false, CancellationToken ct = default)
    {
        try
        {
            return await _resiliencePolicy.ExecuteAsync(async (ctx, token) =>
            {
                var client = await clientFactory.GetClientAsync(token);
                var service = new NativeStripeSubService(client);

                if (immediate)
                {
                    await service.CancelAsync(subscriptionId, cancellationToken: token);
                }
                else
                {
                    await service.UpdateAsync(subscriptionId, new SubscriptionUpdateOptions
                    {
                        CancelAtPeriodEnd = true
                    }, cancellationToken: token);
                }

                // Note: Local DB update and events are handled via webhooks (customer.subscription.deleted/updated)
                // to maintain single source of truth and handle async completion.

                telemetry.TrackEvent("SubscriptionCancellationRequested", new Dictionary<string, string>
                {
                    ["Provider"] = PaymentProvider.Stripe.ToString(),
                    ["SubscriptionId"] = subscriptionId,
                    ["Immediate"] = immediate.ToString()
                });

                return true;
            }, new Context(), ct);
        }
        catch (StripeException ex) when (string.Equals(ex.StripeError?.Code, "resource_missing", StringComparison.Ordinal))
        {
            logger.LogWarning("Stripe subscription {SubscriptionId} not found for cancellation", subscriptionId);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> ResumeAsync(string subscriptionId, CancellationToken ct = default)
    {
        try
        {
            return await _resiliencePolicy.ExecuteAsync(async (ctx, token) =>
            {
                var client = await clientFactory.GetClientAsync(token);
                var service = new NativeStripeSubService(client);

                await service.UpdateAsync(subscriptionId, new SubscriptionUpdateOptions
                {
                    CancelAtPeriodEnd = false
                }, cancellationToken: token);

                telemetry.TrackEvent("SubscriptionResumeRequested", new Dictionary<string, string>
                {
                    ["Provider"] = PaymentProvider.Stripe.ToString(),
                    ["SubscriptionId"] = subscriptionId
                });

                return true;
            }, new Context(), ct);
        }
        catch (StripeException ex) when (string.Equals(ex.StripeError?.Code, "resource_missing", StringComparison.Ordinal))
        {
            logger.LogWarning("Stripe subscription {SubscriptionId} not found for resume", subscriptionId);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task HandleSubscriptionEventAsync(SubscriptionEvent subscriptionEvent, CancellationToken ct = default)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["SubscriptionId"] = subscriptionEvent.SubscriptionId,
            ["EventType"] = subscriptionEvent.EventType.ToString(),
            ["Provider"] = PaymentProvider.Stripe.ToString()
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
            PaymentProvider.Stripe,
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
            Provider = PaymentProvider.Stripe,
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
            existing.ClearCancellation();
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
            Provider = PaymentProvider.Stripe,
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
            Provider = PaymentProvider.Stripe,
            Reason = subscriptionEvent.Metadata.GetValueOrDefault("reason")
        }, ct);
        await paymentRepository.SaveChangesAsync(ct);
    }
}
