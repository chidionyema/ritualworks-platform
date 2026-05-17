using Haworks.BuildingBlocks.Resilience;
using Haworks.BuildingBlocks.Telemetry;
using Haworks.Contracts.Payments;
using Haworks.Payments.Application.Common;
using Haworks.Payments.Application.Interfaces;
using Haworks.Payments.Domain;
using Haworks.Payments.Domain.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Polly;
using Stripe.Checkout;

namespace Haworks.Payments.Infrastructure.Stripe;

/// <summary>
/// Stripe implementation of IPaymentSessionProcessor.
/// Handles payment completion from Stripe webhooks with:
/// - Financial integrity checks with amount tolerance
/// - Atomic concurrency guards for duplicate webhooks
/// - Event-driven order status updates via PaymentCompletedEvent
///
/// Note: This processor does NOT directly update Order entities.
/// Order status updates are handled by consumers in other bounded contexts.
/// </summary>
internal sealed class StripePaymentProcessor(
    IPaymentRepository paymentRepository,
    IStripeClientFactory stripeClientFactory,
    IPaymentSessionCache sessionCache,
    IResiliencePolicyFactory resiliencePolicyFactory,
    IPaymentAmountMismatchHandler amountMismatchHandler,
    IDomainEventPublisher eventPublisher,
    ILogger<StripePaymentProcessor> logger,
    ITelemetryService telemetry) : IPaymentSessionProcessor
{
    private readonly IAsyncPolicy _resiliencePolicy =
        resiliencePolicyFactory.CreateCombinedPolicy(ResilienceOptions.Stripe);

    private const decimal CentMultiplier = 100m;

    /// <inheritdoc />
    public async Task HandleCompletedSessionAsync(
        PaymentSessionEvent sessionEvent,
        CancellationToken ct = default)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["SessionId"] = sessionEvent.SessionId,
            ["TransactionId"] = sessionEvent.TransactionId,
            ["Provider"] = PaymentProvider.Stripe.ToString()
        });

        logger.LogInformation(
            "Processing Stripe payment session {SessionId}",
            sessionEvent.SessionId);

        // 1. Fetch payment record using provider-agnostic method
        var payment = await paymentRepository.GetByProviderSessionTrackedAsync(
            PaymentProvider.Stripe,
            sessionEvent.SessionId,
            ct).ConfigureAwait(false);

        if (payment == null)
        {
            logger.LogError(
                "Payment record missing for Stripe session {SessionId}",
                sessionEvent.SessionId);
            throw new InvalidOperationException(
                $"Payment record missing for Stripe session {sessionEvent.SessionId}");
        }

        // 2. Early exit checks
        if (PaymentValidationHelper.IsAlreadyProcessed(payment))
        {
            logger.LogInformation(
                "Payment {PaymentId} already processed",
                payment.Id);
            return;
        }

        if (PaymentValidationHelper.IsFlaggedForReview(payment))
        {
            logger.LogWarning(
                "Payment {PaymentId} flagged for review",
                payment.Id);
            return;
        }

        // 3. Validate session metadata
        if (!ValidateSessionEventMetadata(sessionEvent, payment))
        {
            logger.LogCritical(
                "Session metadata validation failed for {SessionId}",
                sessionEvent.SessionId);
            throw new InvalidOperationException("Session validation failed - potential tampering");
        }

        // 4. Financial integrity checks
        if (PaymentValidationHelper.HasCurrencyMismatch(sessionEvent.Currency, payment.Currency))
        {
            logger.LogCritical(
                "Currency mismatch for payment {PaymentId}: expected {Expected}, actual {Actual}",
                payment.Id, payment.Currency, sessionEvent.Currency);
            throw new InvalidOperationException(
                $"Currency mismatch: expected {payment.Currency}, received {sessionEvent.Currency}");
        }

        // 4b. Amount validation
        decimal actualPaid = sessionEvent.AmountTotal / CentMultiplier;
        decimal expectedTotal = payment.Amount + payment.Tax;

        if (PaymentValidationHelper.HasAmountMismatch(actualPaid, expectedTotal))
        {
            await HandleAmountMismatchAsync(payment, actualPaid, expectedTotal, ct).ConfigureAwait(false);
            return;
        }

        // 5. Atomic payment completion
        try
        {
            await CompletePaymentAsync(sessionEvent, payment, ct).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            logger.LogWarning(ex, "Concurrency conflict processing session {SessionId} — duplicate webhook, safely ignoring", sessionEvent.SessionId);
        }
    }

    /// <inheritdoc />
    public async Task<bool> ValidateSessionAsync(
        string sessionId,
        string userId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            return false;
        }

        // Check cache first
        var cached = await sessionCache.GetAsync(sessionId, ct).ConfigureAwait(false);
        if (cached != null)
        {
            return sessionCache.ValidateOwnership(cached, userId);
        }

        // Check database
        var payment = await paymentRepository.GetByProviderSessionAsync(
            PaymentProvider.Stripe,
            sessionId,
            ct).ConfigureAwait(false);

        if (payment == null)
        {
            logger.LogWarning("No payment found for session {SessionId}", sessionId);
            return false;
        }

        // Validate ownership
        if (!string.Equals(payment.UserId, userId, StringComparison.Ordinal))
        {
            logger.LogWarning("User {UserId} unauthorized for session", userId);
            return false;
        }

        // Verify with Stripe API using resilience policy.
        // Let transient exceptions (5xx, 429, network) propagate to Polly for retry.
        try
        {
            return await _resiliencePolicy.ExecuteAsync(async (ctx, token) =>
            {
                var client = await stripeClientFactory.GetClientAsync(token).ConfigureAwait(false);
                var session = await new SessionService(client).GetAsync(sessionId, cancellationToken: token).ConfigureAwait(false);

                if (!string.Equals(session.Status, StripeConstants.SessionStatuses.Complete, StringComparison.Ordinal) ||
!string.Equals(session.PaymentStatus, StripeConstants.PaymentStatuses.Paid, StringComparison.Ordinal))
                {
                    logger.LogWarning("Session {SessionId} not complete", sessionId);
                    return false;
                }

                if (session.Metadata?.TryGetValue("orderId", out var orderId) == true &&
!string.Equals(orderId, payment.OrderId.ToString(), StringComparison.Ordinal))
                {
                    logger.LogWarning("OrderId mismatch for session {SessionId}", sessionId);
                    return false;
                }

                await sessionCache.SetAsync(sessionId, payment.OrderId, payment.UserId, token).ConfigureAwait(false);
                return true;
            }, new Context(), ct);
        }
        catch (global::Stripe.StripeException ex) when (IsNonTransientStripeError(ex))
        {
            // Non-transient client errors (4xx except 429) — return false after Polly gives up
            logger.LogError(ex, "Non-transient Stripe error validating session {SessionId}", sessionId);
            return false;
        }
    }

    private async Task CompletePaymentAsync(
        PaymentSessionEvent sessionEvent,
        Payment payment,
        CancellationToken ct)
    {
        try
        {
            // Mark the aggregate as completed
            payment.MarkCompleted(sessionEvent.TransactionId, "card");

            // Publish event for Orders context to handle order status update
            await eventPublisher.PublishAsync(new PaymentCompletedEvent
            {
                PaymentId = payment.Id,
                OrderId = payment.OrderId,
                SagaId = payment.SagaId,
                Amount = payment.Amount,
                Currency = sessionEvent.Currency,
                Provider = PaymentProvider.Stripe.ToString(),
                TransactionReference = sessionEvent.TransactionId
            }, ct).ConfigureAwait(false);

            // Commit changes. MassTransit-EF outbox handles atomicity.
            await paymentRepository.SaveChangesAsync(ct).ConfigureAwait(false);

            await sessionCache.RemoveAsync(sessionEvent.SessionId, ct).ConfigureAwait(false);

            logger.LogInformation(
                "Payment {PaymentId} completed, PaymentCompletedEvent published for order {OrderId}",
                payment.Id, payment.OrderId);
            
            TrackPaymentCompleted(payment.OrderId, payment, sessionEvent);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to complete Stripe Session {SessionId}",
                sessionEvent.SessionId);
            throw;
        }
    }

    private Task HandleAmountMismatchAsync(
        Payment payment,
        decimal actual,
        decimal expected,
        CancellationToken ct)
    {
        return amountMismatchHandler.HandleMismatchAsync(
            payment,
            actual,
            expected,
            PaymentProvider.Stripe,
            ct);
    }

    private static bool ValidateSessionEventMetadata(PaymentSessionEvent sessionEvent, Payment payment)
    {
        if (sessionEvent.Metadata == null || sessionEvent.Metadata.Count == 0)
        {
            return false;
        }

        if (!sessionEvent.Metadata.TryGetValue("orderId", out var sessionOrderId))
        {
            return false;
        }

        return string.Equals(sessionOrderId, payment.OrderId.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns true for non-transient Stripe errors (4xx except 429 rate-limit).
    /// </summary>
    private static bool IsNonTransientStripeError(global::Stripe.StripeException ex)
    {
        var code = ex.HttpStatusCode;
        return code >= System.Net.HttpStatusCode.BadRequest
            && code < System.Net.HttpStatusCode.InternalServerError
            && code != (System.Net.HttpStatusCode)429;
    }

    private void TrackPaymentCompleted(Guid orderId, Payment payment, PaymentSessionEvent sessionEvent)
    {
        telemetry.TrackEvent("PaymentCompleted", new Dictionary<string, string>
        {
            ["Provider"] = PaymentProvider.Stripe.ToString(),
            ["OrderId"] = orderId.ToString(),
            ["PaymentId"] = payment.Id.ToString(),
            ["TransactionId"] = sessionEvent.TransactionId,
            ["Amount"] = payment.Amount.ToString("F2"),
            ["Currency"] = sessionEvent.Currency
        });
    }
}
