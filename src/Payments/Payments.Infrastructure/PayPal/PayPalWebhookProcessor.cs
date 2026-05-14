using System.Text.Json;
using Haworks.Payments.Application.Interfaces;
using Haworks.Contracts.Payments;
using Haworks.BuildingBlocks.Telemetry;
using Haworks.BuildingBlocks.Resilience;
using Haworks.Payments.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using System.Net.Http.Json;

namespace Haworks.Payments.Infrastructure.PayPal;

/// <summary>
/// PayPal implementation of IWebhookProcessor.
/// Validates PayPal webhook signatures and processes events.
/// </summary>
internal sealed class PayPalWebhookProcessor(
    IPaymentSessionProcessor paymentProcessor,
    ISubscriptionManager subscriptionManager,
    IWebhookIdempotencyGuard idempotencyGuard,
    IPayPalClientFactory clientFactory,
    IDomainEventPublisher eventPublisher,
    IResiliencePolicyFactory resiliencePolicyFactory,
    IOptions<PaymentProviderOptions> providerOptions,
    ILogger<PayPalWebhookProcessor> logger,
    ITelemetryService telemetry) : IWebhookProcessor
{
    private readonly PayPalOptions _options = providerOptions.Value.PayPal;
    private readonly IAsyncPolicy _resiliencePolicy = 
        resiliencePolicyFactory.CreateCombinedPolicy(ResilienceOptions.PayPal);

    public PaymentProvider Provider => PaymentProvider.PayPal;

    /// <inheritdoc />
    public async Task<WebhookValidationResult> ValidateAndParseAsync(
        string payload, 
        string signature, 
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(payload)) return WebhookValidationResult.Failure("Empty payload");

        try
        {
            var paypalEvent = JsonSerializer.Deserialize<PayPalWebhookEvent>(payload, PayPalJsonOptions.Default);
            if (paypalEvent == null || string.IsNullOrEmpty(paypalEvent.Id))
            {
                return WebhookValidationResult.Failure("Invalid PayPal webhook payload");
            }

            // Validate signature with PayPal API
            var signatureHeaders = JsonSerializer.Deserialize<PayPalSignatureHeaders>(signature, PayPalJsonOptions.Default);
            if (signatureHeaders == null)
            {
                return WebhookValidationResult.Failure("Missing PayPal signature headers");
            }

            var isValid = await ValidateSignatureWithPayPalAsync(payload, signatureHeaders, ct);
            if (!isValid)
            {
                logger.LogWarning("PayPal webhook signature validation failed for event {EventId}", paypalEvent.Id);
                return WebhookValidationResult.Failure("Webhook signature validation failed");
            }

            var webhookEvent = new PaymentWebhookEvent 
            { 
                EventId = paypalEvent.Id, 
                EventType = paypalEvent.EventType ?? string.Empty, 
                Provider = PaymentProvider.PayPal, 
                CreatedAt = DateTime.UtcNow,
                Data = paypalEvent.Resource,
                RawPayload = payload
            };

            return WebhookValidationResult.Success(webhookEvent);
        }
        catch (Exception ex) 
        {
            logger.LogWarning(ex, "Failed to parse/validate PayPal webhook");
            return WebhookValidationResult.Failure("Invalid PayPal webhook"); 
        }
    }

    public async Task<WebhookProcessingResult> ProcessEventAsync(
        PaymentWebhookEvent webhookEvent, 
        CancellationToken ct = default)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["EventId"] = webhookEvent.EventId,
            ["EventType"] = webhookEvent.EventType,
            ["Provider"] = Provider.ToString()
        });

        if (await idempotencyGuard.IsAlreadyProcessedAsync(Provider, webhookEvent.EventId, ct)) 
            return WebhookProcessingResult.Skipped("Already processed");

        try
        {
            var result = webhookEvent.EventType switch
            {
                PayPalEventTypes.PaymentCaptureCompleted => await HandleCaptureCompletedAsync(webhookEvent, ct),
                PayPalEventTypes.PaymentCaptureRefunded => await HandleCaptureRefundedAsync(webhookEvent, ct),
                PayPalEventTypes.BillingSubscriptionCreated => await HandleSubscriptionEventAsync(webhookEvent, SubscriptionEventType.Created, ct),
                PayPalEventTypes.BillingSubscriptionActivated => await HandleSubscriptionEventAsync(webhookEvent, SubscriptionEventType.Created, ct),
                PayPalEventTypes.BillingSubscriptionUpdated => await HandleSubscriptionEventAsync(webhookEvent, SubscriptionEventType.Updated, ct),
                PayPalEventTypes.BillingSubscriptionCancelled => await HandleSubscriptionEventAsync(webhookEvent, SubscriptionEventType.Canceled, ct),
                PayPalEventTypes.BillingSubscriptionExpired => await HandleSubscriptionEventAsync(webhookEvent, SubscriptionEventType.Expired, ct),
                PayPalEventTypes.BillingSubscriptionReactivated => await HandleSubscriptionEventAsync(webhookEvent, SubscriptionEventType.Resumed, ct),
                PayPalEventTypes.BillingSubscriptionPaymentFailed => await HandleSubscriptionEventAsync(webhookEvent, SubscriptionEventType.PaymentFailed, ct),
                _ => WebhookProcessingResult.Skipped($"Unhandled event type: {webhookEvent.EventType}")
            };

            if (result.Processed)
            {
                await idempotencyGuard.MarkProcessedAsync(Provider, webhookEvent.EventId, webhookEvent.EventType, ct);
            }

            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing PayPal event {EventId}", webhookEvent.EventId);
            telemetry.TrackException(ex);
            throw;
        }
    }

    private async Task<bool> ValidateSignatureWithPayPalAsync(
        string payload,
        PayPalSignatureHeaders headers,
        CancellationToken ct)
    {
        return await _resiliencePolicy.ExecuteAsync(async (ctx, token) =>
        {
            try
            {
                var client = await clientFactory.GetAuthenticatedClientAsync(token);

                var verifyRequest = new PayPalVerifySignatureRequest
                {
                    WebhookId = _options.WebhookId,
                    TransmissionId = headers.TransmissionId,
                    TransmissionTime = headers.TransmissionTime,
                    TransmissionSig = headers.TransmissionSig,
                    CertUrl = headers.CertUrl,
                    AuthAlgo = headers.AuthAlgo,
                    WebhookEvent = JsonDocument.Parse(payload)
                };

                var response = await client.PostAsJsonAsync(
                    PayPalEndpoints.VerifyWebhookSignature,
                    verifyRequest,
                    PayPalJsonOptions.Default,
                    token);

                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = await response.Content.ReadAsStringAsync(token);
                    logger.LogError("PayPal signature verification failed: {Body}", errorBody);
                    return false;
                }

                var result = await response.Content.ReadFromJsonAsync<PayPalVerifySignatureResponse>(
                    PayPalJsonOptions.Default,
                    token);

                return result?.VerificationStatus?.Equals(PayPalVerificationStatuses.Success, StringComparison.OrdinalIgnoreCase) == true;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "PayPal signature verification exception");
                return false;
            }
        }, new Context(), ct);
    }

    private async Task<WebhookProcessingResult> HandleCaptureCompletedAsync(PaymentWebhookEvent webhookEvent, CancellationToken ct)
    {
        var resource = (JsonElement)webhookEvent.Data!;
        var captureId = resource.GetProperty("id").GetString()!;
        var amount = resource.GetProperty("amount").GetProperty("value").GetString()!;
        var currency = resource.GetProperty("amount").GetProperty("currency_code").GetString()!;
        
        // order_id is in supplementary_data.related_ids.order_id
        var orderId = resource.TryGetProperty("supplementary_data", out var supp) 
            && supp.TryGetProperty("related_ids", out var ids)
            ? ids.GetProperty("order_id").GetString()!
            : captureId;

        var sessionEvent = new PaymentSessionEvent 
        { 
            SessionId = orderId, 
            TransactionId = captureId, 
            Provider = PaymentProvider.PayPal, 
            Mode = SessionMode.Payment,
            Currency = currency,
            AmountTotal = (long)(decimal.Parse(amount) * CheckoutConstants.CentMultiplier) 
        };

        await paymentProcessor.HandleCompletedSessionAsync(sessionEvent, ct);
        return WebhookProcessingResult.Success(webhookEvent.EventType, "Capture completed handled");
    }

    private async Task<WebhookProcessingResult> HandleCaptureRefundedAsync(PaymentWebhookEvent webhookEvent, CancellationToken ct)
    {
        var resource = (JsonElement)webhookEvent.Data!;
        var refundId = resource.GetProperty("id").GetString()!;
        var status = resource.GetProperty("status").GetString()!;
        
        if (status.Equals("COMPLETED", StringComparison.OrdinalIgnoreCase))
        {
            var amount = resource.GetProperty("amount").GetProperty("value").GetString()!;
            
            // Try to find saga ID in custom_id or invoice_id
            string? sagaIdStr = null;
            if (resource.TryGetProperty("custom_id", out var customId)) sagaIdStr = customId.GetString();
            else if (resource.TryGetProperty("invoice_id", out var invoiceId)) sagaIdStr = invoiceId.GetString();

            if (Guid.TryParse(sagaIdStr, out var sagaId))
            {
                await eventPublisher.PublishAsync(new ProviderRefundSucceededEvent
                {
                    RefundId = sagaId,
                    ProviderRefundId = refundId,
                    AmountRefunded = decimal.Parse(amount),
                    CompletedAt = DateTime.UtcNow
                }, ct);
            }
        }

        return WebhookProcessingResult.Success(webhookEvent.EventType, $"Refund {refundId} processed");
    }

    private async Task<WebhookProcessingResult> HandleSubscriptionEventAsync(PaymentWebhookEvent webhookEvent, SubscriptionEventType eventType, CancellationToken ct)
    {
        var resource = (JsonElement)webhookEvent.Data!;
        var subId = resource.GetProperty("id").GetString()!;
        
        var subEvent = new SubscriptionEvent
        {
            SubscriptionId = subId,
            EventType = eventType,
            NewStatus = MapPayPalSubscriptionStatus(resource.GetProperty("status").GetString()!, eventType),
            Provider = Provider,
            UserId = resource.TryGetProperty("subscriber", out var subscriber) ? subscriber.GetProperty("email_address").GetString() : null,
            PlanId = resource.GetProperty("plan_id").GetString()
        };

        if (resource.TryGetProperty("billing_info", out var billingInfo) && billingInfo.TryGetProperty("next_billing_time", out var nextTime))
        {
            subEvent = subEvent with { CurrentPeriodEnd = DateTime.Parse(nextTime.GetString()!) };
        }

        await subscriptionManager.HandleSubscriptionEventAsync(subEvent, ct);
        return WebhookProcessingResult.Success(webhookEvent.EventType, $"Subscription {subId} processed");
    }

    private static SubscriptionStatus MapPayPalSubscriptionStatus(string? status, SubscriptionEventType eventType)
    {
        if (eventType == SubscriptionEventType.Canceled) return SubscriptionStatus.Canceled;
        if (eventType == SubscriptionEventType.Expired) return SubscriptionStatus.Expired;
        if (eventType == SubscriptionEventType.PaymentFailed) return SubscriptionStatus.PastDue;

        return status?.ToUpperInvariant() switch
        {
            "ACTIVE" => SubscriptionStatus.Active,
            "CANCELLED" => SubscriptionStatus.Canceled,
            "SUSPENDED" => SubscriptionStatus.PastDue,
            "EXPIRED" => SubscriptionStatus.Expired,
            _ => SubscriptionStatus.Unknown
        };
    }
}
