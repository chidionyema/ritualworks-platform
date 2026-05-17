namespace Haworks.Contracts.Pricing;

public sealed record PricingRequestedEvent : DomainEvent
{
    public required Guid SagaId { get; init; }
    public required Guid OrderId { get; init; }
    public required Guid ProductId { get; init; }
    public required int Quantity { get; init; }
    public string? PromoCode { get; init; }
    public required string UserId { get; init; }
    public string? CountryCode { get; init; }
    public string? StateCode { get; init; }
}
