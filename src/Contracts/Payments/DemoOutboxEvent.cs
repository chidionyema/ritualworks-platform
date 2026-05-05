namespace Haworks.Contracts.Payments;

/// <summary>
/// T2.5 contract: BffWeb's portfolio event-flow demo writes one of these
/// to payments-svc's outbox when the user clicks "Trigger Event". The
/// EF outbox + MassTransit relay handles persistence -> broker -> delivery,
/// and BffWeb's own consumer translates the inbound message back to a
/// SignalR OnEventFlow stage='consumed' event so the UI can animate the
/// full persisted -> relayed -> consumed lifecycle against real
/// distributed plumbing.
///
/// Lives in Contracts/Payments because payments-svc is the publisher
/// (T2.5 has the admin endpoint there). Consumers may live anywhere
/// (BffWeb is the only one today).
/// </summary>
public sealed record DemoOutboxEvent : DomainEvent
{
    /// <summary>The portfolio session id correlating SignalR push to the
    /// browser group.</summary>
    public required Guid SessionId { get; init; }

    /// <summary>Opaque demo payload — frontend renders verbatim.</summary>
    public string? Payload { get; init; }

    // EventId + OccurredAt come from DomainEvent base — frontend matches
    // notifications by EventId across the persisted -> consumed stages.
}
