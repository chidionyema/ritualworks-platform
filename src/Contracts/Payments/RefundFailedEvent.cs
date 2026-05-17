namespace Haworks.Contracts.Payments;

public sealed record RefundFailedEvent : DomainEvent
{
    public required Guid RefundId { get; init; }
    public required Guid OrderId { get; init; }
    public required string FailureCategory { get; init; }
    public required string FailureDetail { get; init; }
    public string? CustomerEmail { get; init; }
}
