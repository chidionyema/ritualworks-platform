using Haworks.BuildingBlocks.Messaging;
using Haworks.BuildingBlocks.Telemetry;
using Haworks.Payments.Application.Interfaces;
using Haworks.Contracts.Payments;
using Haworks.Payments.Domain.Interfaces;
using Haworks.Payments.Domain.Interfaces;
using Haworks.Payments.Infrastructure.Options;
using Haworks.Payments.Infrastructure.Stripe;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Stripe;
using Stripe.Checkout;
using Xunit;

namespace Haworks.Payments.Unit;

public class StripeWebhookProcessorTests
{
    private readonly Mock<IPaymentSessionProcessor> _paymentProcessorMock;
    private readonly Mock<ISubscriptionManager> _subscriptionManagerMock;
    private readonly Mock<IRefundService> _refundServiceMock;
    private readonly Mock<IWebhookIdempotencyGuard> _idempotencyGuardMock;
    private readonly Mock<IPaymentRepository> _paymentRepositoryMock;
    private readonly Mock<IDomainEventPublisher> _eventPublisherMock;
    private readonly Mock<ILogger<StripeWebhookProcessor>> _loggerMock;
    private readonly Mock<ITelemetryService> _telemetryMock;
    private readonly IOptions<PaymentProviderOptions> _options;
    private readonly StripeWebhookProcessor _processor;

    public StripeWebhookProcessorTests()
    {
        _paymentProcessorMock = new Mock<IPaymentSessionProcessor>();
        _subscriptionManagerMock = new Mock<ISubscriptionManager>();
        _refundServiceMock = new Mock<IRefundService>();
        _idempotencyGuardMock = new Mock<IWebhookIdempotencyGuard>();
        _paymentRepositoryMock = new Mock<IPaymentRepository>();
        _eventPublisherMock = new Mock<IDomainEventPublisher>();
        _loggerMock = new Mock<ILogger<StripeWebhookProcessor>>();
        _telemetryMock = new Mock<ITelemetryService>();

        _options = Options.Create(new PaymentProviderOptions
        {
            Active = PaymentProvider.Stripe,
            Stripe = new StripeOptions
            {
                SecretKey = "sk_test_123",
                WebhookSecret = "whsec_test_123"
            }
        });

        _processor = new StripeWebhookProcessor(
            _paymentProcessorMock.Object,
            _subscriptionManagerMock.Object,
            _refundServiceMock.Object,
            _idempotencyGuardMock.Object,
            _paymentRepositoryMock.Object,
            _eventPublisherMock.Object,
            _options,
            _loggerMock.Object,
            _telemetryMock.Object);
    }

    [Fact]
    public void Provider_ReturnsStripe()
    {
        Assert.Equal(PaymentProvider.Stripe, _processor.Provider);
    }

    [Fact]
    public async Task ValidateAndParseAsync_EmptyPayload_ReturnsFailure()
    {
        var result = await _processor.ValidateAndParseAsync("", "sig", CancellationToken.None);
        Assert.False(result.IsValid);
        Assert.Equal("Empty payload", result.ErrorMessage);
    }

    [Fact]
    public async Task ValidateAndParseAsync_PayloadMissingIdOrType_ReturnsFailure()
    {
        // Signature validation lives in StripeSignatureValidator at the
        // controller boundary; the processor only parses the payload envelope.
        var result = await _processor.ValidateAndParseAsync("{}", "sig", CancellationToken.None);
        Assert.False(result.IsValid);
        Assert.Contains("id", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProcessEventAsync_AlreadyProcessedEvent_ReturnsSkipped()
    {
        var webhookEvent = new PaymentWebhookEvent
        {
            EventId = "evt_123",
            EventType = "checkout.session.completed",
            Provider = PaymentProvider.Stripe,
            CreatedAt = DateTime.UtcNow
        };

        _idempotencyGuardMock
            .Setup(g => g.IsAlreadyProcessedAsync(PaymentProvider.Stripe, "evt_123", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await _processor.ProcessEventAsync(webhookEvent, CancellationToken.None);

        Assert.False(result.Processed);
        Assert.Equal("Already processed", result.Message);
        _paymentProcessorMock.Verify(
            p => p.HandleCompletedSessionAsync(It.IsAny<PaymentSessionEvent>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ProcessEventAsync_CheckoutSessionCompleted_PaymentMode_ProcessesPayment()
    {
        // Processor parses session details from RawPayload (the original
        // webhook body), not from the SDK's strongly-typed Data object.
        var orderId = Guid.NewGuid().ToString();
        var rawPayload = $$"""
            {
              "id": "evt_123",
              "type": "checkout.session.completed",
              "data": {
                "object": {
                  "id": "cs_123",
                  "object": "checkout.session",
                  "mode": "payment",
                  "payment_intent": "pi_123",
                  "amount_total": 1000,
                  "currency": "usd",
                  "metadata": { "orderId": "{{orderId}}" }
                }
              }
            }
            """;

        var webhookEvent = new PaymentWebhookEvent
        {
            EventId = "evt_123",
            EventType = "checkout.session.completed",
            Provider = PaymentProvider.Stripe,
            CreatedAt = DateTime.UtcNow,
            RawPayload = rawPayload
        };

        _idempotencyGuardMock
            .Setup(g => g.IsAlreadyProcessedAsync(It.IsAny<PaymentProvider>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        PaymentSessionEvent? capturedEvent = null;
        _paymentProcessorMock
            .Setup(p => p.HandleCompletedSessionAsync(It.IsAny<PaymentSessionEvent>(), It.IsAny<CancellationToken>()))
            .Callback<PaymentSessionEvent, CancellationToken>((e, _) => capturedEvent = e)
            .Returns(Task.CompletedTask);

        var result = await _processor.ProcessEventAsync(webhookEvent, CancellationToken.None);

        Assert.True(result.Processed);
        Assert.NotNull(capturedEvent);
        Assert.Equal("cs_123", capturedEvent.SessionId);
        Assert.Equal("pi_123", capturedEvent.TransactionId);
        Assert.Equal(1000, capturedEvent.AmountTotal);
        Assert.Equal("usd", capturedEvent.Currency);
        Assert.Equal(PaymentProvider.Stripe, capturedEvent.Provider);
        Assert.Equal(SessionMode.Payment, capturedEvent.Mode);
    }
}
