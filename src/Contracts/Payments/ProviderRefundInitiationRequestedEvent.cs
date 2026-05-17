namespace Haworks.Contracts.Payments;

public sealed record ProviderRefundInitiationRequestedEvent : DomainEvent
{
    public required Guid RefundId { get; init; }
    public required string Provider { get; init; }
    public required Guid PaymentId { get; init; }
    public required decimal Amount { get; init; }
    public required string Currency { get; init; }
}
