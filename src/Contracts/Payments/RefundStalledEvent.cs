namespace Haworks.Contracts.Payments;

public sealed record RefundStalledEvent : DomainEvent
{
    public required Guid RefundId { get; init; }
    public required int HoursSinceRequest { get; init; }
}
