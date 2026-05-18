namespace Haworks.Contracts.Payments;

public sealed record RefundRequestedEvent : DomainEvent
{
    public required Guid RefundId { get; init; }
    public required Guid OrderId { get; init; }
    public required Guid PaymentId { get; init; }
    public required long AmountCents { get; init; }
    public required string Currency { get; init; }
    public string? Reason { get; init; }
    public string? RequestedBy { get; init; }
    public string? Provider { get; init; }
}
