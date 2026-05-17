namespace Haworks.Contracts.Payments;

public sealed record RefundCancelledByOperatorEvent : DomainEvent
{
    public required Guid RefundId { get; init; }
}
