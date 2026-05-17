namespace Haworks.Contracts.Payments;

public sealed record SubscriptionPaymentRecoveredEvent : DomainEvent
{
    public required Guid SubscriptionId { get; init; }
    public required DateTime RecoveredAt { get; init; } = DateTime.UtcNow;
}
