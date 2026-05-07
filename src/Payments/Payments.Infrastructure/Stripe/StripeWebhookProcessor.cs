using Haworks.BuildingBlocks.Telemetry;
using Haworks.Contracts.Payments;
using Haworks.Payments.Application.Interfaces;
using Haworks.Payments.Domain;
using Haworks.Payments.Domain.Interfaces;
using Haworks.Payments.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stripe;
using Stripe.Checkout;

namespace Haworks.Payments.Infrastructure.Stripe;

/// <summary>
/// Stripe implementation of IWebhookProcessor.
/// Validates Stripe webhook signatures and processes events.
/// </summary>
internal sealed class StripeWebhookProcessor : IWebhookProcessor
{
    private readonly IPaymentSessionProcessor _paymentProcessor;
    private readonly IWebhookIdempotencyGuard _idempotencyGuard;
    private readonly IPaymentRepository _paymentRepository;
    private readonly IDomainEventPublisher _eventPublisher;
    private readonly ILogger<StripeWebhookProcessor> _logger;
    private readonly ITelemetryService _telemetry;
    private readonly StripeOptions _stripeOptions;

    public PaymentProvider Provider => PaymentProvider.Stripe;

    public StripeWebhookProcessor(
        IPaymentSessionProcessor paymentProcessor,
        IWebhookIdempotencyGuard idempotencyGuard,
        IPaymentRepository paymentRepository,
        IDomainEventPublisher eventPublisher,
        IOptions<PaymentProviderOptions> providerOptions,
        ILogger<StripeWebhookProcessor> logger,
        ITelemetryService telemetry)
    {
        _paymentProcessor = paymentProcessor ?? throw new ArgumentNullException(nameof(paymentProcessor));
        _idempotencyGuard = idempotencyGuard ?? throw new ArgumentNullException(nameof(idempotencyGuard));
        _paymentRepository = paymentRepository ?? throw new ArgumentNullException(nameof(paymentRepository));
        _eventPublisher = eventPublisher ?? throw new ArgumentNullException(nameof(eventPublisher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
        _stripeOptions = providerOptions?.Value?.Stripe ?? throw new ArgumentNullException(nameof(providerOptions));
    }

    /// <inheritdoc />
    public Task<WebhookValidationResult> ValidateAndParseAsync(
        string payload,
        string signature,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(payload))
        {
            return Task.FromResult(WebhookValidationResult.Failure("Empty payload"));
        }

        if (string.IsNullOrEmpty(signature))
        {
            return Task.FromResult(WebhookValidationResult.Failure("Missing Stripe-Signature header"));
        }

        try
        {
            var stripeEvent = EventUtility.ConstructEvent(
                payload,
                signature,
                _stripeOptions.WebhookSecret,
                throwOnApiVersionMismatch: false);

            _logger.LogDebug(
                "Validated Stripe webhook event {EventId} of type {EventType}",
                stripeEvent.Id,
                stripeEvent.Type);

            var webhookEvent = new PaymentWebhookEvent
            {
                EventId = stripeEvent.Id,
                EventType = stripeEvent.Type,
                Provider = PaymentProvider.Stripe,
                CreatedAt = stripeEvent.Created,
                Data = stripeEvent.Data.Object,
                RawPayload = payload
            };

            return Task.FromResult(WebhookValidationResult.Success(webhookEvent));
        }
        catch (StripeException ex)
        {
            _logger.LogWarning(ex, "Stripe webhook signature validation failed");
            return Task.FromResult(WebhookValidationResult.Failure($"Invalid signature: {ex.Message}"));
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

        // Check idempotency
        if (await _idempotencyGuard.IsAlreadyProcessedAsync(Provider, webhookEvent.EventId, ct))
        {
            _logger.LogInformation(
                "Stripe event {EventId} already processed, skipping",
                webhookEvent.EventId);
            return WebhookProcessingResult.Skipped("Already processed");
        }

        try
        {
            var result = webhookEvent.EventType switch
            {
                StripeConstants.EventTypes.CheckoutSessionCompleted => await HandleCheckoutSessionCompletedAsync(webhookEvent, ct),
                StripeConstants.EventTypes.CheckoutSessionExpired => await HandleCheckoutSessionExpiredAsync(webhookEvent, ct),
                _ => HandleUnknownEvent(webhookEvent)
            };

            // Mark as processed if successful
            if (result.Processed)
            {
                await _idempotencyGuard.MarkProcessedAsync(
                    Provider,
                    webhookEvent.EventId,
                    webhookEvent.EventType,
                    ct);
            }

            TrackWebhookEvent(webhookEvent, result);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Error processing Stripe event {EventId} of type {EventType}",
                webhookEvent.EventId,
                webhookEvent.EventType);

            _telemetry.TrackException(ex);
            throw;
        }
    }

    private async Task<WebhookProcessingResult> HandleCheckoutSessionCompletedAsync(
        PaymentWebhookEvent webhookEvent,
        CancellationToken ct)
    {
        if (webhookEvent.Data is not Session session)
        {
            return WebhookProcessingResult.Failed("Session data is null");
        }

        if (session.Mode != StripeConstants.SessionModes.Payment)
        {
            _logger.LogWarning("Unhandled session mode: {Mode}", session.Mode);
            return WebhookProcessingResult.Skipped($"Unhandled session mode: {session.Mode}");
        }

        var sessionEvent = new PaymentSessionEvent
        {
            SessionId = session.Id,
            TransactionId = session.PaymentIntentId ?? string.Empty,
            Mode = SessionMode.Payment,
            AmountTotal = session.AmountTotal ?? 0,
            Currency = session.Currency ?? "USD",
            Provider = PaymentProvider.Stripe,
            Metadata = session.Metadata != null
                ? new Dictionary<string, string>(session.Metadata)
                : new Dictionary<string, string>()
        };

        await _paymentProcessor.HandleCompletedSessionAsync(sessionEvent, ct);

        return WebhookProcessingResult.Success(
            StripeConstants.EventTypes.CheckoutSessionCompleted,
            $"Payment session {session.Id} processed");
    }

    private async Task<WebhookProcessingResult> HandleCheckoutSessionExpiredAsync(
        PaymentWebhookEvent webhookEvent,
        CancellationToken ct)
    {
        if (webhookEvent.Data is not Session session)
        {
            return WebhookProcessingResult.Failed("Session data is null");
        }

        var payment = await _paymentRepository.GetByProviderSessionAsync(
            PaymentProvider.Stripe,
            session.Id,
            ct).ConfigureAwait(false);

        if (payment == null)
        {
            return WebhookProcessingResult.Skipped("Payment not found for session");
        }

        if (payment.IsComplete)
        {
            return WebhookProcessingResult.Skipped("Payment already completed");
        }

        await _eventPublisher.PublishAsync(new CheckoutSessionExpiredEvent
        {
            PaymentId = payment.Id,
            OrderId = payment.OrderId,
            SessionId = session.Id,
            Provider = Provider.ToString()
        }, ct).ConfigureAwait(false);

        return WebhookProcessingResult.Success(
            StripeConstants.EventTypes.CheckoutSessionExpired,
            $"Checkout session expired event published for session {session.Id}");
    }

    private WebhookProcessingResult HandleUnknownEvent(PaymentWebhookEvent webhookEvent)
    {
        _logger.LogInformation(
            "Unhandled Stripe event type: {EventType}",
            webhookEvent.EventType);

        return WebhookProcessingResult.Skipped($"Unhandled event type: {webhookEvent.EventType}");
    }

    private void TrackWebhookEvent(PaymentWebhookEvent webhookEvent, WebhookProcessingResult result)
    {
        _telemetry.TrackEvent("WebhookProcessed", new Dictionary<string, string>
        {
            ["Provider"] = Provider.ToString(),
            ["EventType"] = webhookEvent.EventType,
            ["EventId"] = webhookEvent.EventId,
            ["Processed"] = result.Processed.ToString()
        });
    }
}
