using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Haworks.Contracts.Payments;

namespace Haworks.Payments.Application.Consumers;

/// <summary>
/// Demo-mode payment consumer. Orchestrates the payment flow by mocking
/// the provider's behavior. In DemoMode, it automatically transitions
/// from session creation to completion (or failure if requested).
/// </summary>
public sealed class PaymentSessionRequestedConsumer(
    IConfiguration configuration,
    IDomainEventPublisher eventPublisher,
    ILogger<PaymentSessionRequestedConsumer> logger
) : IConsumer<PaymentSessionRequestedEvent>
{
    public async Task Consume(ConsumeContext<PaymentSessionRequestedEvent> context)
    {
        var evt = context.Message;
        var isDemoMode = configuration.GetValue<bool>("Payments:DemoMode", true);

        if (!isDemoMode)
        {
            // Production path via Stripe is not yet implemented.
            throw new NotImplementedException(
                "Production mode (Stripe) is not implemented. Set Payments:DemoMode=true.");
        }

        logger.LogInformation(
            "Processing PaymentSessionRequestedEvent in DEMO MODE. OrderId={OrderId}, SagaId={SagaId}",
            evt.OrderId, evt.SagaId);

        var sessionId = Guid.NewGuid().ToString();
        var paymentId = Guid.NewGuid();
        var provider = "demo-mock";

        // 1. Create session immediately
        await eventPublisher.PublishAsync(new PaymentSessionCreatedEvent
        {
            OrderId = evt.OrderId,
            SagaId = evt.SagaId,
            PaymentId = paymentId,
            SessionId = sessionId,
            CheckoutUrl = $"https://demo.haworks.dev/checkout/{sessionId}",
            Provider = provider,
            Amount = evt.Amount,
            Currency = evt.Currency
        }, context.CancellationToken);

        // 2. Delay to simulate provider processing
        await Task.Delay(1000, context.CancellationToken);

        // 3. Complete or Fail based on scenario
        var isFailureScenario = evt.IdempotencyKey?.Contains("paymentFailure", StringComparison.OrdinalIgnoreCase) ?? false;

        if (isFailureScenario)
        {
            logger.LogWarning(
                "Simulating payment failure for OrderId={OrderId}, SagaId={SagaId} (scenario detected in IdempotencyKey)",
                evt.OrderId, evt.SagaId);

            await eventPublisher.PublishAsync(new PaymentSessionFailedEvent
            {
                OrderId = evt.OrderId,
                SagaId = evt.SagaId,
                Provider = provider,
                ErrorCode = "payment_session_failed",
                ErrorMessage = "Card declined — your items are released",
                AttemptNumber = 1,
                IsFinalAttempt = true
            }, context.CancellationToken);
        }
        else
        {
            logger.LogInformation(
                "Completing payment for OrderId={OrderId}, SagaId={SagaId}",
                evt.OrderId, evt.SagaId);

            await eventPublisher.PublishAsync(new PaymentCompletedEvent
            {
                PaymentId = paymentId,
                OrderId = evt.OrderId,
                SagaId = evt.SagaId,
                Amount = evt.Amount,
                Currency = evt.Currency,
                Provider = provider,
                TransactionReference = $"demo-ref-{Guid.NewGuid():N}"
            }, context.CancellationToken);
        }
    }
}
