namespace Haworks.Contracts.Payments;

public sealed record ProviderRefundInitiatedEvent : DomainEvent
{
    public required Guid RefundId { get; init; }
    public required string ProviderRefundId { get; init; }
}
