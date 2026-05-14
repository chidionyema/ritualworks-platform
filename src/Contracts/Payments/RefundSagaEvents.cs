namespace Haworks.Contracts.Payments;

public sealed record RefundRequestedEvent : DomainEvent
{
    public required Guid RefundId { get; init; }
    public required Guid OrderId { get; init; }
    public required Guid PaymentId { get; init; }
    public required decimal Amount { get; init; }
    public required string Currency { get; init; }
    public string? Reason { get; init; }
    public string? RequestedBy { get; init; }
}

public sealed record ProviderRefundInitiationRequestedEvent : DomainEvent
{
    public required Guid RefundId { get; init; }
    public required string Provider { get; init; }
    public required Guid PaymentId { get; init; }
    public required decimal Amount { get; init; }
    public required string Currency { get; init; }
}

public sealed record ProviderRefundInitiatedEvent : DomainEvent
{
    public required Guid RefundId { get; init; }
    public required string ProviderRefundId { get; init; }
}

public sealed record ProviderRefundSucceededEvent : DomainEvent
{
    public required Guid RefundId { get; init; }
    public required string ProviderRefundId { get; init; }
    public required decimal AmountRefunded { get; init; }
    public required DateTime CompletedAt { get; init; }
}

public sealed record ProviderRefundFailedEvent : DomainEvent
{
    public required Guid RefundId { get; init; }
    public required string ErrorCode { get; init; }
    public required string ErrorMessage { get; init; }
}

public sealed record ProviderRefundCancellationRequestedEvent : DomainEvent
{
    public required Guid RefundId { get; init; }
    public required string ProviderRefundId { get; init; }
}

public sealed record RefundCompletedEvent : DomainEvent
{
    public required Guid RefundId { get; init; }
    public required Guid OrderId { get; init; }
    public required Guid PaymentId { get; init; }
    public required decimal Amount { get; init; }
    public required string Currency { get; init; }
}

public sealed record RefundFailedEvent : DomainEvent
{
    public required Guid RefundId { get; init; }
    public required Guid OrderId { get; init; }
    public required string FailureCategory { get; init; }
    public required string FailureDetail { get; init; }
}

public sealed record RefundStalledEvent : DomainEvent
{
    public required Guid RefundId { get; init; }
    public required int HoursSinceRequest { get; init; }
}

public sealed record RefundCancelledEvent : DomainEvent
{
    public required Guid RefundId { get; init; }
    public required Guid OrderId { get; init; }
    public required string Reason { get; init; }
}

public sealed record RefundTimedOutEvent : DomainEvent
{
    public required Guid RefundId { get; init; }
}

public sealed record RefundCancelledByOperatorEvent : DomainEvent
{
    public required Guid RefundId { get; init; }
}
