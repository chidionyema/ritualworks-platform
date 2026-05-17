using Haworks.Payments.Application.Interfaces;
using Haworks.Payments.Domain.Interfaces;
using Haworks.BuildingBlocks.Telemetry;
using Haworks.BuildingBlocks.Resilience;
using Haworks.Contracts.Payments;
using Microsoft.Extensions.Logging;
using Polly;
using Stripe;

namespace Haworks.Payments.Infrastructure.Stripe;

/// <summary>
/// Stripe implementation of IRefundService.
/// Handles refund operations using the Stripe Refunds API.
/// </summary>
internal sealed class StripeRefundService(
    IStripeClientFactory clientFactory,
    IPaymentRepository paymentRepository,
    IDomainEventPublisher eventPublisher,
    IResiliencePolicyFactory resiliencePolicyFactory,
    ILogger<StripeRefundService> logger,
    ITelemetryService telemetry) : IRefundService
{
    private readonly IAsyncPolicy _resiliencePolicy = 
        resiliencePolicyFactory.CreateCombinedPolicy(ResilienceOptions.Stripe);

    /// <inheritdoc />
    public async Task<RefundResult> CreateRefundAsync(
        RefundRequest request, 
        CancellationToken ct = default)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["TransactionId"] = request.TransactionId,
            ["AmountCents"] = request.AmountCents?.ToString() ?? "full",
            ["Provider"] = PaymentProvider.Stripe.ToString()
        });

        // Only the Stripe API call belongs inside the retry block.
        // DB reads and event publishing are non-idempotent side-effects
        // that must happen AFTER Polly exhausts retries.
        global::Stripe.Refund refund;
        try
        {
            refund = await _resiliencePolicy.ExecuteAsync(async (ctx, token) =>
            {
                var client = await clientFactory.GetClientAsync(token);
                var service = new RefundService(client);

                var options = new RefundCreateOptions
                {
                    PaymentIntent = request.TransactionId,
                    Reason = MapRefundReason(request.Reason),
                    Metadata = request.Metadata ?? new Dictionary<string, string>()
                };

                if (request.AmountCents.HasValue)
                {
                    options.Amount = request.AmountCents.Value;
                }

                var requestOptions = new RequestOptions
                {
                    IdempotencyKey = !string.IsNullOrWhiteSpace(request.IdempotencyKey)
                        ? request.IdempotencyKey
                        : Guid.NewGuid().ToString()
                };

                logger.LogInformation(
                    "Creating Stripe refund for PaymentIntent {TransactionId}",
                    request.TransactionId);

                return await service.CreateAsync(options, requestOptions, token);
            }, new Context(), ct);
        }
        catch (StripeException ex) when (IsNonTransientStripeError(ex))
        {
            // Non-transient client errors (4xx except 429) — no retry will help
            logger.LogError(ex,
                "Non-transient Stripe error creating refund for PaymentIntent {TransactionId}",
                request.TransactionId);

            return new RefundResult
            {
                RefundId = string.Empty,
                Status = RefundStatus.Failed,
                AmountCents = request.AmountCents ?? 0,
                FailureReason = ex.StripeError?.Message ?? ex.Message,
                Provider = PaymentProvider.Stripe
            };
        }

        logger.LogInformation(
            "Stripe refund {RefundId} created with status {Status}",
            refund.Id,
            refund.Status);

        // DB read + event publish happen outside the retry block
        var payment = await paymentRepository.GetByProviderTransactionIdAsync(request.TransactionId, ct);

        if (payment != null && string.Equals(refund.Status, "succeeded", StringComparison.Ordinal))
        {
            await eventPublisher.PublishAsync(new RefundIssuedEvent
            {
                PaymentId = payment.Id,
                OrderId = payment.OrderId,
                RefundId = refund.Id,
                AmountCents = refund.Amount,
                Currency = payment.Currency,
                Provider = PaymentProvider.Stripe,
                Reason = request.Reason
            }, ct);
        }

        TrackRefundEvent("RefundCreated", refund.Id, request.TransactionId, MapRefundStatus(refund.Status));

        return new RefundResult
        {
            RefundId = refund.Id,
            Status = MapRefundStatus(refund.Status),
            AmountCents = refund.Amount,
            Provider = PaymentProvider.Stripe
        };
    }

    /// <inheritdoc />
    public async Task<RefundResult> GetRefundStatusAsync(
        string refundId, 
        CancellationToken ct = default)
    {
        var client = await clientFactory.GetClientAsync(ct);
        var service = new RefundService(client);
        
        try
        {
            var refund = await service.GetAsync(refundId, cancellationToken: ct);
            return new RefundResult 
            { 
                RefundId = refund.Id, 
                Status = MapRefundStatus(refund.Status), 
                AmountCents = refund.Amount,
                Provider = PaymentProvider.Stripe 
            };
        }
        catch (StripeException ex)
        {
            logger.LogError(ex, "Error retrieving Stripe refund status for {RefundId}", refundId);
            return new RefundResult
            {
                RefundId = refundId,
                Status = RefundStatus.Failed,
                FailureReason = ex.Message,
                Provider = PaymentProvider.Stripe
            };
        }
    }

    private static RefundStatus MapRefundStatus(string stripeStatus)
    {
        return stripeStatus?.ToLowerInvariant() switch
        {
            "pending" => RefundStatus.Pending,
            "succeeded" => RefundStatus.Succeeded,
            "failed" => RefundStatus.Failed,
            "canceled" => RefundStatus.Canceled,
            "requires_action" => RefundStatus.RequiresAction,
            _ => RefundStatus.Pending
        };
    }

    private static string? MapRefundReason(string? reason)
    {
        if (string.IsNullOrEmpty(reason)) return null;

        return reason.ToLowerInvariant() switch
        {
            "duplicate" => "duplicate",
            "fraudulent" => "fraudulent",
            "requested_by_customer" or "customer_request" => "requested_by_customer",
            _ => null
        };
    }

    /// <summary>
    /// Returns true for non-transient Stripe errors (4xx except 429 rate-limit).
    /// These should NOT be retried by Polly.
    /// </summary>
    private static bool IsNonTransientStripeError(StripeException ex)
    {
        var code = ex.HttpStatusCode;
        return code >= System.Net.HttpStatusCode.BadRequest
            && code < System.Net.HttpStatusCode.InternalServerError
            && code != (System.Net.HttpStatusCode)429;
    }

    private void TrackRefundEvent(string eventName, string refundId, string transactionId, RefundStatus status)
    {
        telemetry.TrackEvent(eventName, new Dictionary<string, string>
        {
            ["Provider"] = PaymentProvider.Stripe.ToString(),
            ["RefundId"] = refundId,
            ["TransactionId"] = transactionId,
            ["Status"] = status.ToString()
        });
    }
}
