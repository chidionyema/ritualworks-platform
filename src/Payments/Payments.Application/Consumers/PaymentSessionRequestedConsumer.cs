using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Haworks.Contracts.Payments;
using Haworks.Payments.Application.Telemetry;

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
    ICheckoutSessionService checkoutService,
    Haworks.Payments.Domain.Interfaces.IPaymentRepository paymentRepository,
    ILogger<PaymentSessionRequestedConsumer> logger
) : IConsumer<PaymentSessionRequestedEvent>
{
    public async Task Consume(ConsumeContext<PaymentSessionRequestedEvent> context)
    {
        var evt = context.Message;
        var isDemoMode = configuration.GetValue<bool>("Payments:DemoMode", false);

        using var activity = PaymentsActivities.Source.StartActivity("payments.session.create");
        activity?.SetTag("order.id", evt.OrderId);
        activity?.SetTag("saga.id", evt.SagaId);
        activity?.SetTag("payment.amount_cents", (long)Math.Round(evt.Amount * 100m, 0, MidpointRounding.AwayFromZero));
        activity?.SetTag("payment.currency", evt.Currency);
        activity?.SetTag("payment.provider", isDemoMode ? "demo-mock" : "stripe");
        activity?.SetTag("payment.demo_mode", isDemoMode);

        if (isDemoMode)
        {
            await HandleDemoModeAsync(context, evt);
            return;
        }

        logger.LogInformation(
            "Processing PaymentSessionRequestedEvent via Stripe. OrderId={OrderId}, SagaId={SagaId}",
            evt.OrderId, evt.SagaId);

        try
        {
            // 1. Create the Payment aggregate (Status = Pending)
            var payment = Haworks.Payments.Domain.Payment.Create(
                evt.OrderId,
                evt.UserId,
                evt.Amount,
                evt.Tax,
                evt.Currency,
                Haworks.Contracts.Payments.PaymentProvider.Stripe,
                evt.SagaId);

            await paymentRepository.AddAsync(payment, context.CancellationToken);

            // 2. Request session from provider
            var request = new CreateCheckoutSessionRequest
            {
                SuccessUrl = evt.SuccessUrl,
                CancelUrl = evt.CancelUrl,
                CustomerEmail = evt.CustomerEmail,
                IdempotencyKey = evt.IdempotencyKey ?? Guid.NewGuid().ToString(),
                LineItems = evt.LineItems.Select(li => new LineItem
                {
                    Name = li.Name,
                    Description = li.Description,
                    UnitAmountCents = li.UnitAmountCents,
                    Quantity = li.Quantity,
                    Currency = evt.Currency
                }).ToList(),
                OrderId = evt.OrderId,
                Metadata = evt.Metadata?.ToDictionary(x => x.Key, x => x.Value) ?? new Dictionary<string, string>()
            };

            var result = await checkoutService.CreateSessionAsync(request, context.CancellationToken);

            // 3. Update payment with provider details (Status = Processing)
            payment.AttachProviderSession(result.SessionId, result.SessionUrl);
            await paymentRepository.SaveChangesAsync(context.CancellationToken);

            // 4. Notify saga
            await eventPublisher.PublishAsync(new PaymentSessionCreatedEvent
            {
                OrderId = evt.OrderId,
                SagaId = evt.SagaId,
                PaymentId = payment.Id,
                SessionId = result.SessionId,
                CheckoutUrl = result.SessionUrl,
                Provider = Haworks.Contracts.Payments.PaymentProvider.Stripe.ToString(),
                Amount = evt.Amount,
                Currency = evt.Currency
            }, context.CancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create Stripe payment session for OrderId={OrderId}", evt.OrderId);

            await eventPublisher.PublishAsync(new PaymentSessionFailedEvent
            {
                OrderId = evt.OrderId,
                SagaId = evt.SagaId,
                Provider = Haworks.Contracts.Payments.PaymentProvider.Stripe.ToString(),
                ErrorCode = "provider_error",
                ErrorMessage = ex.Message,
                AttemptNumber = 1,
                IsFinalAttempt = true
            }, context.CancellationToken);

            throw; // let MassTransit retry
        }
    }

    private async Task HandleDemoModeAsync(ConsumeContext<PaymentSessionRequestedEvent> context, PaymentSessionRequestedEvent evt)
    {
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
