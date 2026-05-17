namespace Haworks.Contracts.Pricing;

public sealed record PromotionRedeemedEvent : DomainEvent
{
    public required Guid OrderId { get; init; }
    public required string Code { get; init; }
    public required decimal DiscountAmount { get; init; }
    public string? UserId { get; init; }
}
