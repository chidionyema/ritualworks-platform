namespace Haworks.Contracts.Pricing;

/// <summary>
/// Published when a pricing calculation is requested by the CheckoutSaga.
/// </summary>
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

/// <summary>
/// Published when pricing calculation completes successfully.
/// </summary>
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

/// <summary>
/// Published when pricing calculation fails (tax lookup failure, catalog unavailable, etc.).
/// </summary>
public sealed record PricingFailedEvent : DomainEvent
{
    public required Guid SagaId { get; init; }
    public required Guid OrderId { get; init; }
    public required string Reason { get; init; }
}

/// <summary>
/// Published when a promotion code is successfully redeemed.
/// </summary>
public sealed record PromotionRedeemedEvent : DomainEvent
{
    public required Guid OrderId { get; init; }
    public required string Code { get; init; }
    public required decimal DiscountAmount { get; init; }
    public string? UserId { get; init; }
}
