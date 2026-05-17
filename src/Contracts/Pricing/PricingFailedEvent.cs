namespace Haworks.Contracts.Pricing;

public sealed record PricingFailedEvent : DomainEvent
{
    public required Guid SagaId { get; init; }
    public required Guid OrderId { get; init; }
    public required string Reason { get; init; }
}
