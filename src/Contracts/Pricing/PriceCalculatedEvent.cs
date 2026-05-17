namespace Haworks.Contracts.Pricing;

public sealed record PriceCalculatedEvent : DomainEvent
{
    public required Guid SagaId { get; init; }
    public required Guid OrderId { get; init; }
    public required Guid CalculationId { get; init; }
    public required decimal Subtotal { get; init; }
    public required decimal TaxAmount { get; init; }
    public required decimal Total { get; init; }
    public required string Currency { get; init; }
}
