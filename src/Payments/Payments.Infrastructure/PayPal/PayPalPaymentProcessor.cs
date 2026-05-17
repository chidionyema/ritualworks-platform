using Haworks.BuildingBlocks.Resilience;
using Haworks.BuildingBlocks.Telemetry;
using Haworks.Contracts.Payments;
using Haworks.Payments.Application.Common;
using Haworks.Payments.Application.Interfaces;
using Haworks.Payments.Domain;
using Haworks.Payments.Domain.Interfaces;
using Microsoft.Extensions.Logging;
using Polly;
using System.Net.Http.Json;
using System.Text.Json;

namespace Haworks.Payments.Infrastructure.PayPal;

/// <summary>
/// PayPal implementation of IPaymentSessionProcessor.
/// Handles payment completion from PayPal webhooks.
/// </summary>
internal sealed class PayPalPaymentProcessor(
    IPaymentRepository paymentRepository,
    IPayPalClientFactory paypalClientFactory,
    IResiliencePolicyFactory resiliencePolicyFactory,
    IPaymentAmountMismatchHandler amountMismatchHandler,
    IDomainEventPublisher eventPublisher,
    ILogger<PayPalPaymentProcessor> logger,
    ITelemetryService telemetry) : IPaymentSessionProcessor
{
    private readonly IAsyncPolicy _resiliencePolicy =
        resiliencePolicyFactory.CreateCombinedPolicy(ResilienceOptions.PayPal);

    /// <inheritdoc />
    public async Task HandleCompletedSessionAsync(
        PaymentSessionEvent sessionEvent,
        CancellationToken ct = default)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["SessionId"] = sessionEvent.SessionId,
            ["TransactionId"] = sessionEvent.TransactionId,
            ["Provider"] = PaymentProvider.PayPal.ToString()
        });

        logger.LogInformation(
            "Processing PayPal payment session {SessionId}",
            sessionEvent.SessionId);

        // 1. Fetch payment record. PayPal sessionId is the order ID.
        var payment = await paymentRepository.GetByProviderSessionTrackedAsync(
            PaymentProvider.PayPal,
            sessionEvent.SessionId,
            ct).ConfigureAwait(false);

        if (payment == null)
        {
            logger.LogError(
                "Payment record missing for PayPal session {SessionId}",
                sessionEvent.SessionId);
            throw new InvalidOperationException(
                $"Payment record missing for PayPal session {sessionEvent.SessionId}");
        }

        // 2. Early exit checks
        if (PaymentValidationHelper.IsAlreadyProcessed(payment))
        {
            logger.LogInformation("Payment {PaymentId} already processed", payment.Id);
            return;
        }

        // 3. Validate metadata if available
        if (sessionEvent.Metadata.TryGetValue("orderId", out var orderId) && !string.Equals(orderId, payment.OrderId.ToString(), StringComparison.Ordinal))
        {
            logger.LogCritical("OrderId mismatch for session {SessionId}", sessionEvent.SessionId);
            throw new InvalidOperationException("Session validation failed");
        }

        // 4. Financial integrity checks
        decimal actualPaid = sessionEvent.AmountTotal / CheckoutConstants.CentMultiplier;
        decimal expectedTotal = payment.Amount + payment.Tax;

        if (PaymentValidationHelper.HasAmountMismatch(actualPaid, expectedTotal))
        {
            await amountMismatchHandler.HandleMismatchAsync(payment, actualPaid, expectedTotal, PaymentProvider.PayPal, ct).ConfigureAwait(false);
            return;
        }

        // 5. Atomic payment completion
        payment.MarkCompleted(sessionEvent.TransactionId, "paypal");

        await eventPublisher.PublishAsync(new PaymentCompletedEvent
        {
            PaymentId = payment.Id,
            OrderId = payment.OrderId,
            SagaId = payment.SagaId,
            Amount = payment.Amount,
            Currency = sessionEvent.Currency,
            Provider = PaymentProvider.PayPal.ToString(),
            TransactionReference = sessionEvent.TransactionId
        }, ct).ConfigureAwait(false);

        await paymentRepository.SaveChangesAsync(ct).ConfigureAwait(false);

        logger.LogInformation("Payment {PaymentId} completed for order {OrderId}", payment.Id, payment.OrderId);
        TrackPaymentCompleted(payment.OrderId, payment, sessionEvent);
    }

    /// <inheritdoc />
    public async Task<bool> ValidateSessionAsync(
        string sessionId,
        string userId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(sessionId)) return false;

        var payment = await paymentRepository.GetByProviderSessionAsync(PaymentProvider.PayPal, sessionId, ct).ConfigureAwait(false);
        if (payment == null || !string.Equals(payment.UserId, userId, StringComparison.Ordinal)) return false;

        // Let transient exceptions (5xx, 429, network) propagate to Polly for retry.
        try
        {
            return await _resiliencePolicy.ExecuteAsync(async (ctx, token) =>
            {
                var client = await paypalClientFactory.GetAuthenticatedClientAsync(token).ConfigureAwait(false);
                var response = await client.GetAsync(PayPalEndpoints.GetOrder(sessionId), token).ConfigureAwait(false);

                // Non-transient client errors: return false without retry
                if (!response.IsSuccessStatusCode)
                {
                    var code = (int)response.StatusCode;
                    if (code >= 400 && code < 500 && code != 429)
                        return false;

                    // Transient server error: throw so Polly retries
                    response.EnsureSuccessStatusCode();
                }

                var responseStream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                var order = await JsonSerializer.DeserializeAsync<PayPalOrder>(responseStream, PayPalJsonOptions.Default, token).ConfigureAwait(false);
                return string.Equals(order?.Status, PayPalOrderStatuses.Completed, StringComparison.Ordinal) || string.Equals(order?.Status, PayPalOrderStatuses.Approved, StringComparison.Ordinal);
            }, new Context(), ct);
        }
        catch (HttpRequestException ex)
        {
            // Polly exhausted retries for transient errors
            logger.LogError(ex, "PayPal API error validating session {SessionId} after retries exhausted", sessionId);
            return false;
        }
    }

    private void TrackPaymentCompleted(Guid orderId, Payment payment, PaymentSessionEvent sessionEvent)
    {
        telemetry.TrackEvent("PaymentCompleted", new Dictionary<string, string>
        {
            ["Provider"] = PaymentProvider.PayPal.ToString(),
            ["OrderId"] = orderId.ToString(),
            ["PaymentId"] = payment.Id.ToString(),
            ["TransactionId"] = sessionEvent.TransactionId,
            ["Amount"] = payment.Amount.ToString("F2"),
            ["Currency"] = sessionEvent.Currency
        });
    }
}
