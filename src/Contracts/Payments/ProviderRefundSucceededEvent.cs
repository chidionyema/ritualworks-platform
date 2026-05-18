namespace Haworks.Contracts.Payments;

public sealed record ProviderRefundSucceededEvent : DomainEvent
{
    public required Guid RefundId { get; init; }
    public required string ProviderRefundId { get; init; }
    public required long AmountRefundedCents { get; init; }
    public required DateTime CompletedAt { get; init; }
}
