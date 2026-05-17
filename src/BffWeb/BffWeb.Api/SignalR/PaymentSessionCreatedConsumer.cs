using MassTransit;
using Microsoft.AspNetCore.SignalR;
using Haworks.Contracts.Payments;

namespace Haworks.BffWeb.Api.SignalR;

/// <summary>
/// Bridges the saga's PaymentSessionCreated event to a SignalR push.
/// Lives in the bff-web process because (a) it owns the SignalR hub and
/// (b) it has no other reason to consume payments-svc events. Per ADR-0009
/// it touches no foreign-context state — the message carries everything
/// needed (CheckoutUrl, SessionId, Provider).
///
/// Failure mode: if no client is currently subscribed to the SagaId
/// group (browser closed, race against subscribe), the SendAsync call
/// silently no-ops. The browser can recover via GET /api/checkouts/{sagaId}
/// to read the persisted session state from checkout-svc.
/// </summary>
public sealed class PaymentSessionCreatedConsumer(
    IHubContext<CheckoutHub> hub,
    ILogger<PaymentSessionCreatedConsumer> logger
) : IConsumer<PaymentSessionCreatedEvent>
{
    public async Task Consume(ConsumeContext<PaymentSessionCreatedEvent> context)
    {
        var evt = context.Message;
        logger.LogInformation(
            "Pushing CheckoutReady to SignalR group for sagaId={SagaId}, paymentId={PaymentId}",
            evt.SagaId, evt.PaymentId);

        try
        {
            await hub.Clients.Group(CheckoutHub.GroupNameFor(evt.UserId, evt.SagaId))
                .SendAsync("CheckoutReady", new
                {
                    sagaId = evt.SagaId,
                    orderId = evt.OrderId,
                    paymentId = evt.PaymentId,
                    checkoutUrl = evt.CheckoutUrl,
                    provider = evt.Provider,
                    amount = evt.Amount,
                    currency = evt.Currency,
                }, context.CancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to push CheckoutReady to SignalR group for sagaId={SagaId}", evt.SagaId);
            throw;
        }
    }
}
