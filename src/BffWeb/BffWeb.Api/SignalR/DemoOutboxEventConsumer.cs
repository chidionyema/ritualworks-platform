using Haworks.BffWeb.Application.Interfaces;
using Haworks.Contracts.Payments;
using MassTransit;

namespace Haworks.BffWeb.Api.SignalR;

/// <summary>
/// T2.5 — bridges the payments-svc DemoOutboxEvent (relayed via RabbitMQ
/// from the EF outbox) to a SignalR OnEventFlow stage='consumed' push.
/// Closes the persisted -> consumed loop the frontend animates: BffWeb
/// emits 'persisted' inline when the admin endpoint responds, payments
/// commits + relays, this consumer emits 'consumed' as the message lands.
///
/// The 'relayed' intermediate stage is NOT emitted today — would require
/// IDemoHubNotifier to be injected into MassTransit's outbox dispatcher,
/// which is BuildingBlocks/Messaging plumbing out of scope for T2.5.
/// </summary>
public sealed class DemoOutboxEventConsumer(
    IDemoHubNotifier notifier,
    ILogger<DemoOutboxEventConsumer> logger) : IConsumer<DemoOutboxEvent>
{
    public Task Consume(ConsumeContext<DemoOutboxEvent> ctx)
    {
        var evt = ctx.Message;
        logger.LogDebug(
            "Bridging DemoOutboxEvent -> OnEventFlow consumed for session={SessionId} eventId={EventId}",
            evt.SessionId, evt.EventId);

        return notifier.NotifyEventFlowAsync(new EventFlowEvent(
            SessionId: evt.SessionId,
            EventId: evt.EventId.ToString(),
            Stage: "consumed",
            Data: evt.Payload,
            Timestamp: DateTime.UtcNow), ctx.CancellationToken);
    }
}
