using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Haworks.Contracts.Payments;

namespace Haworks.Payments.Application.Consumers;

/// <summary>
/// Saga choreography role: drives the
/// <c>StockReserved → ReadyForPayment → Completed | Compensating</c>
/// transitions of <c>CheckoutSaga</c> (in
/// src/CheckoutOrchestrator/CheckoutOrchestrator.Application/Sagas/
/// CheckoutSaga.cs — no project reference per ADR-0009 bounded-context
/// isolation, so the cref is text-only).
///
/// When the saga lands in StockReserved (CheckoutSaga.cs around line 100,
/// "During(Initiated, When(StockReserved)...)") it publishes
/// <see cref="PaymentSessionRequestedEvent"/>. This consumer picks that
/// event up from RabbitMQ and either:
///
/// <list type="bullet">
///   <item>Publishes <see cref="PaymentSessionCreatedEvent"/> followed by
///         <see cref="PaymentCompletedEvent"/> — the saga transitions
///         StockReserved → ReadyForPayment → Completed (final).</item>
///   <item>Publishes <see cref="PaymentSessionFailedEvent"/> — saga
///         transitions to Compensating, which publishes a
///         <c>StockReleaseRequestedEvent</c> back to catalog so the
///         reserved units return to inventory.</item>
/// </list>
///
/// Demo-mode behaviour: failure scenarios are encoded by the BFF as a
/// substring in <c>IdempotencyKey</c> ("paymentFailure"); production
/// will replace this branch with a real Stripe Checkout API call. The
/// scenario-tag pattern lets the saga + frontend exercise the full
/// compensation path without a real Stripe outage.
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
