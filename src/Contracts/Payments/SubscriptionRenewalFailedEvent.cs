namespace Haworks.Contracts.Payments;

public sealed record SubscriptionRenewalFailedEvent : DomainEvent
{
    public required Guid SubscriptionId { get; init; }
    public required string ErrorCode { get; init; }
    public required string ErrorMessage { get; init; }
}
