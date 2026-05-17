namespace Haworks.Contracts.Payments;

public sealed record SubscriptionGracePeriodStartedEvent : DomainEvent
{
    public required Guid SubscriptionId { get; init; }
    public required DateTime ExpiresAt { get; init; }
}
