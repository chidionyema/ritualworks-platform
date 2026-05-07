using Haworks.BuildingBlocks.Messaging;
using Haworks.BuildingBlocks.Telemetry;
using Haworks.Payments.Application.Interfaces;
using Haworks.Payments.Domain;
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
    public async Task ValidateAndParseAsync_MissingSignature_ReturnsFailure()
    {
        var result = await _processor.ValidateAndParseAsync("{}", "", CancellationToken.None);
        Assert.False(result.IsValid);
        Assert.Equal("Missing Stripe-Signature header", result.ErrorMessage);
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
        var session = new Session
        {
            Id = "cs_123",
            Mode = "payment",
            PaymentIntentId = "pi_123",
            AmountTotal = 1000,
            Currency = "usd",
            Metadata = new Dictionary<string, string> { ["orderId"] = Guid.NewGuid().ToString() }
        };

        var webhookEvent = new PaymentWebhookEvent
        {
            EventId = "evt_123",
            EventType = "checkout.session.completed",
            Provider = PaymentProvider.Stripe,
            CreatedAt = DateTime.UtcNow,
            Data = session
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
