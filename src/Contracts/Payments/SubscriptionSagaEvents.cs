namespace Haworks.Contracts.Payments;

public sealed record SubscriptionRenewalRequestedEvent : DomainEvent
{
    public required Guid SubscriptionId { get; init; }
    public required string ProviderSubscriptionId { get; init; }
}

public sealed record SubscriptionRenewalFailedEvent : DomainEvent
{
    public required Guid SubscriptionId { get; init; }
    public required string ErrorCode { get; init; }
    public required string ErrorMessage { get; init; }
}

public sealed record SubscriptionDunningRetryScheduledEvent : DomainEvent
{
    public required Guid SubscriptionId { get; init; }
    public required int RetryCount { get; init; }
    public required DateTime ScheduledAt { get; init; }
}

public sealed record SubscriptionPaymentRecoveredEvent : DomainEvent
{
    public required Guid SubscriptionId { get; init; }
    public required DateTime RecoveredAt { get; init; } = DateTime.UtcNow;
}

public sealed record SubscriptionGracePeriodStartedEvent : DomainEvent
{
    public required Guid SubscriptionId { get; init; }
    public required DateTime ExpiresAt { get; init; }
}
