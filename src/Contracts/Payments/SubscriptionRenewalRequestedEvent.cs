namespace Haworks.Contracts.Payments;

public sealed record SubscriptionRenewalRequestedEvent : DomainEvent
{
    public required Guid SubscriptionId { get; init; }
    public required string ProviderSubscriptionId { get; init; }
}
