namespace Haworks.Contracts.Payments;

public sealed record RefundApprovedByOperatorEvent : DomainEvent
{
    public required Guid RefundId { get; init; }
}
