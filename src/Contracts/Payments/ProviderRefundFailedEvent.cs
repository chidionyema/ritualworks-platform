namespace Haworks.Contracts.Payments;

public sealed record ProviderRefundFailedEvent : DomainEvent
{
    public required Guid RefundId { get; init; }
    public required string ErrorCode { get; init; }
    public required string ErrorMessage { get; init; }
}
