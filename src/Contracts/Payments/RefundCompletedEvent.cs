namespace Haworks.Contracts.Payments;

public sealed record RefundCompletedEvent : DomainEvent
{
    public required Guid RefundId { get; init; }
    public required Guid OrderId { get; init; }
    public required Guid PaymentId { get; init; }
    public required decimal Amount { get; init; }
    public required string Currency { get; init; }
    public string? CustomerEmail { get; init; }
}
