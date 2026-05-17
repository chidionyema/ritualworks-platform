namespace Haworks.Contracts.Payments;

public sealed record SubscriptionDunningRetryScheduledEvent : DomainEvent
{
    public required Guid SubscriptionId { get; init; }
    public required int RetryCount { get; init; }
    public required DateTime ScheduledAt { get; init; }
}
