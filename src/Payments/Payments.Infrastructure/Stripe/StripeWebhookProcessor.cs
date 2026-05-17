using Haworks.BuildingBlocks.Telemetry;
using Haworks.Contracts.Payments;
using Haworks.Payments.Application.Interfaces;
using Haworks.Payments.Domain;
using Haworks.Payments.Domain.Interfaces;
using Haworks.Payments.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Haworks.Payments.Infrastructure.Stripe;

/// <summary>
/// Validates Stripe webhook signatures and processes events.
/// Uses internal DTOs to shield business logic from SDK-specific serialization quirks.
/// </summary>
internal sealed class StripeWebhookProcessor : IWebhookProcessor
{
    private readonly IPaymentSessionProcessor _paymentProcessor;
    private readonly ISubscriptionManager _subscriptionManager;
    private readonly IWebhookIdempotencyGuard _idempotencyGuard;
    private readonly IPaymentRepository _paymentRepository;
    private readonly IDomainEventPublisher _eventPublisher;
    private readonly ILogger<StripeWebhookProcessor> _logger;
    private readonly ITelemetryService _telemetry;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public PaymentProvider Provider => PaymentProvider.Stripe;

    public StripeWebhookProcessor(
        IPaymentSessionProcessor paymentProcessor,
        ISubscriptionManager subscriptionManager,
        IWebhookIdempotencyGuard idempotencyGuard,
        IPaymentRepository paymentRepository,
        IDomainEventPublisher eventPublisher,
        IOptions<PaymentProviderOptions> providerOptions,
        ILogger<StripeWebhookProcessor> logger,
        ITelemetryService telemetry)
    {
        _paymentProcessor = paymentProcessor ?? throw new ArgumentNullException(nameof(paymentProcessor));
        _subscriptionManager = subscriptionManager ?? throw new ArgumentNullException(nameof(subscriptionManager));
        _idempotencyGuard = idempotencyGuard ?? throw new ArgumentNullException(nameof(idempotencyGuard));
        _paymentRepository = paymentRepository ?? throw new ArgumentNullException(nameof(paymentRepository));
        _eventPublisher = eventPublisher ?? throw new ArgumentNullException(nameof(eventPublisher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
    }

    /// <inheritdoc />
    public Task<WebhookValidationResult> ValidateAndParseAsync(
        string payload,
        string signature,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(payload)) return Task.FromResult(WebhookValidationResult.Failure("Empty payload"));

        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;

            var id = root.GetProperty("id").GetString();
            var type = root.GetProperty("type").GetString();

            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(type))
            {
                return Task.FromResult(WebhookValidationResult.Failure("Missing id or type in payload"));
            }

            var webhookEvent = new PaymentWebhookEvent
            {
                EventId = id,
                EventType = type,
                Provider = Provider,
                CreatedAt = DateTime.UtcNow,
                RawPayload = payload
            };

            return Task.FromResult(WebhookValidationResult.Success(webhookEvent));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse Stripe webhook payload");
            return Task.FromResult(WebhookValidationResult.Failure($"Invalid payload: {ex.Message}"));
        }
    }

    /// <inheritdoc />
    public async Task<WebhookProcessingResult> ProcessEventAsync(
        PaymentWebhookEvent webhookEvent,
        CancellationToken ct = default)
    {
        using var scope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["EventId"] = webhookEvent.EventId,
            ["EventType"] = webhookEvent.EventType,
            ["Provider"] = Provider.ToString()
        });

        if (await _idempotencyGuard.IsAlreadyProcessedAsync(Provider, webhookEvent.EventId, ct))
        {
            return WebhookProcessingResult.Skipped("Already processed");
        }

        try
        {
            var result = webhookEvent.EventType switch
            {
                StripeConstants.EventTypes.CheckoutSessionCompleted => await HandleCheckoutSessionCompletedAsync(webhookEvent, ct),
                StripeConstants.EventTypes.CheckoutSessionExpired => await HandleCheckoutSessionExpiredAsync(webhookEvent, ct),
                StripeConstants.EventTypes.CustomerSubscriptionCreated => await HandleSubscriptionEventAsync(webhookEvent, SubscriptionEventType.Created, ct),
                StripeConstants.EventTypes.CustomerSubscriptionUpdated => await HandleSubscriptionEventAsync(webhookEvent, SubscriptionEventType.Updated, ct),
                StripeConstants.EventTypes.CustomerSubscriptionDeleted => await HandleSubscriptionEventAsync(webhookEvent, SubscriptionEventType.Canceled, ct),
                StripeConstants.EventTypes.InvoicePaymentFailed => await HandleInvoicePaymentFailedAsync(webhookEvent, ct),
                "charge.refunded" => await HandleChargeRefundedAsync(webhookEvent, ct),
                _ => WebhookProcessingResult.Skipped($"Unhandled event type: {webhookEvent.EventType}")
            };

            if (result.Processed)
            {
                await _idempotencyGuard.MarkProcessedAsync(Provider, webhookEvent.EventId, webhookEvent.EventType, ct);
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing Stripe event {EventId}", webhookEvent.EventId);
            _telemetry.TrackException(ex);
            throw;
        }
    }

    private async Task<WebhookProcessingResult> HandleCheckoutSessionCompletedAsync(PaymentWebhookEvent webhookEvent, CancellationToken ct)
    {
        var session = ParseDataObject<StripeSessionDto>(webhookEvent.RawPayload);
        if (session == null) return WebhookProcessingResult.Failed("Failed to parse Session data");

        if (string.Equals(session.Mode, "subscription", StringComparison.Ordinal))
        {
            var subEvent = new SubscriptionEvent
            {
                SubscriptionId = session.SubscriptionId ?? string.Empty,
                EventType = SubscriptionEventType.Created,
                NewStatus = SubscriptionStatus.Active,
                UserId = session.Metadata.GetValueOrDefault("userId") ?? session.Metadata.GetValueOrDefault("user_id") ?? string.Empty,
                Provider = Provider,
                Metadata = session.Metadata
            };
            await _subscriptionManager.HandleSubscriptionEventAsync(subEvent, ct);
        }
        else
        {
            var sessionEvent = new PaymentSessionEvent
            {
                SessionId = session.Id,
                TransactionId = session.PaymentIntentId ?? string.Empty,
                Mode = SessionMode.Payment,
                AmountTotal = session.AmountTotal ?? 0,
                Currency = session.Currency ?? "USD",
                Provider = Provider,
                Metadata = session.Metadata
            };
            await _paymentProcessor.HandleCompletedSessionAsync(sessionEvent, ct);
        }

        return WebhookProcessingResult.Success(webhookEvent.EventType, $"Session {session.Id} processed");
    }

    private async Task<WebhookProcessingResult> HandleSubscriptionEventAsync(PaymentWebhookEvent webhookEvent, SubscriptionEventType eventType, CancellationToken ct)
    {
        var sub = ParseDataObject<StripeSubscriptionDto>(webhookEvent.RawPayload);
        if (sub == null) return WebhookProcessingResult.Failed("Failed to parse Subscription data");

        var subscriptionEvent = new SubscriptionEvent
        {
            SubscriptionId = sub.Id,
            EventType = eventType,
            NewStatus = StripeSubscriptionStatusMapper.FromStripeStatus(sub.Status ?? string.Empty),
            CurrentPeriodEnd = DateTimeOffset.FromUnixTimeSeconds(sub.CurrentPeriodEnd).UtcDateTime,
            PlanId = sub.Items?.Data?.FirstOrDefault()?.Price?.Id,
            Provider = Provider,
            Metadata = sub.Metadata
        };

        await _subscriptionManager.HandleSubscriptionEventAsync(subscriptionEvent, ct);
        return WebhookProcessingResult.Success(webhookEvent.EventType, $"Subscription {sub.Id} processed");
    }

    private async Task<WebhookProcessingResult> HandleInvoicePaymentFailedAsync(PaymentWebhookEvent webhookEvent, CancellationToken ct)
    {
        var invoice = ParseDataObject<StripeInvoiceDto>(webhookEvent.RawPayload);
        if (invoice == null) return WebhookProcessingResult.Failed("Failed to parse Invoice data");
        if (string.IsNullOrEmpty(invoice.Subscription)) return WebhookProcessingResult.Skipped("Not a subscription invoice");

        var subscriptionEvent = new SubscriptionEvent
        {
            SubscriptionId = invoice.Subscription,
            EventType = SubscriptionEventType.PaymentFailed,
            NewStatus = SubscriptionStatus.PastDue,
            Provider = Provider
        };

        await _subscriptionManager.HandleSubscriptionEventAsync(subscriptionEvent, ct);
        return WebhookProcessingResult.Success(webhookEvent.EventType, "Invoice failed handled");
    }

    private async Task<WebhookProcessingResult> HandleCheckoutSessionExpiredAsync(PaymentWebhookEvent webhookEvent, CancellationToken ct)
    {
        var session = ParseDataObject<StripeSessionDto>(webhookEvent.RawPayload);
        if (session == null) return WebhookProcessingResult.Failed("Failed to parse Session data");

        var payment = await _paymentRepository.GetByProviderSessionAsync(Provider, session.Id, ct);
        if (payment == null) return WebhookProcessingResult.Skipped("No payment record");

        // Publish then save — outbox message commits atomically with entity state
        await _eventPublisher.PublishAsync(new CheckoutSessionExpiredEvent
        {
            SessionId = session.Id,
            PaymentId = payment.Id,
            OrderId = payment.OrderId,
            Provider = Provider.ToString()
        }, ct);
        await _paymentRepository.SaveChangesAsync(ct);

        return WebhookProcessingResult.Success(webhookEvent.EventType, "Expired handled");
    }

    private async Task<WebhookProcessingResult> HandleChargeRefundedAsync(PaymentWebhookEvent webhookEvent, CancellationToken ct)
    {
        var charge = ParseDataObject<StripeChargeDto>(webhookEvent.RawPayload);
        if (charge == null) return WebhookProcessingResult.Failed("Failed to parse Charge data");
        if (string.IsNullOrEmpty(charge.PaymentIntent)) return WebhookProcessingResult.Skipped("Missing PaymentIntent reference");

        // Stripe sends charge.refunded even for partial refunds.
        // The business logic for domain event publishing is already in StripeRefundService.CreateRefundAsync,
        // but this handles refunds triggered from the Stripe Dashboard.
        var payment = await _paymentRepository.GetByProviderTransactionIdAsync(charge.PaymentIntent, ct);
        if (payment == null) return WebhookProcessingResult.Skipped("No payment record for intent");

        // We check if it's already processed or if we need to emit a system-wide event.
        // For Dashboard refunds, we want to ensure Orders etc are notified.
        foreach (var refund in charge.Refunds.Data.Where(r => string.Equals(r.Status, "succeeded", StringComparison.Ordinal)))
        {
            // 1. Legacy/Dashboard event for broad consumption
            await _eventPublisher.PublishAsync(new RefundIssuedEvent
            {
                PaymentId = payment.Id,
                OrderId = payment.OrderId,
                RefundId = refund.Id,
                AmountCents = refund.Amount,
                Currency = charge.Currency,
                Provider = Provider,
                Reason = "Refunded via Stripe"
            }, ct);

            // 2. Saga-specific correlation event
            if (refund.Metadata.TryGetValue("refund_id", out var sagaIdStr) && Guid.TryParse(sagaIdStr, out var sagaId))
            {
                await _eventPublisher.PublishAsync(new ProviderRefundSucceededEvent
                {
                    RefundId = sagaId,
                    ProviderRefundId = refund.Id,
                    AmountRefunded = refund.Amount / 100m,
                    CompletedAt = DateTime.UtcNow
                }, ct);
            }
        }

        await _paymentRepository.SaveChangesAsync(ct);
        return WebhookProcessingResult.Success(webhookEvent.EventType, "Refund processed");
    }

    private T? ParseDataObject<T>(string rawPayload) where T : class
    {
        try
        {
            using var doc = JsonDocument.Parse(rawPayload);
            var dataElement = doc.RootElement.GetProperty("data").GetProperty("object");
            return JsonSerializer.Deserialize<T>(dataElement.GetRawText(), JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to manually parse data object of type {Type}", typeof(T).Name);
            return null;
        }
    }

    private sealed class StripeSessionDto
    {
        public string Id { get; set; } = string.Empty;
        public string? Mode { get; set; }
        [JsonPropertyName("payment_intent")]
        public string? PaymentIntentId { get; set; }
        [JsonPropertyName("subscription")]
        public string? SubscriptionId { get; set; }
        public long? AmountTotal { get; set; }
        public string? Currency { get; set; }
        public Dictionary<string, string> Metadata { get; set; } = new();
    }

    private sealed class StripeSubscriptionDto
    {
        public string Id { get; set; } = string.Empty;
        public string? Status { get; set; }
        public long CurrentPeriodEnd { get; set; }
        public StripeSubscriptionItemsDto? Items { get; set; }
        public Dictionary<string, string> Metadata { get; set; } = new();
    }

    private sealed class StripeSubscriptionItemsDto
    {
        public List<StripeSubscriptionItemDto> Data { get; set; } = new();
    }

    private sealed class StripeSubscriptionItemDto
    {
        public StripePriceDto? Price { get; set; }
    }

    private sealed class StripePriceDto
    {
        public string Id { get; set; } = string.Empty;
    }

    private sealed class StripeInvoiceDto
    {
        public string? Subscription { get; set; }
    }

    private sealed class StripeChargeDto
    {
        public string Id { get; set; } = string.Empty;
        public string? PaymentIntent { get; set; }
        public string Currency { get; set; } = string.Empty;
        public StripeRefundsListDto Refunds { get; set; } = new();
    }

    private sealed class StripeRefundsListDto
    {
        public List<StripeRefundDto> Data { get; set; } = new();
    }

    private sealed class StripeRefundDto
    {
        public string Id { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public long Amount { get; set; }
        public Dictionary<string, string> Metadata { get; set; } = new();
    }
}
