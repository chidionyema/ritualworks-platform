namespace Haworks.Contracts.Payments;

public sealed record RefundTimedOutEvent : DomainEvent
{
    public required Guid RefundId { get; init; }
}
