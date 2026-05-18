namespace Haworks.Contracts.Pricing;

public sealed record PriceCalculatedEvent : DomainEvent
{
    public required Guid SagaId { get; init; }
    public required Guid OrderId { get; init; }
    public required Guid CalculationId { get; init; }
    public required long SubtotalCents { get; init; }
    public required long TaxCents { get; init; }
    public required long TotalCents { get; init; }
    public required string Currency { get; init; }
}
