namespace Haworks.Contracts.Merchant;

public sealed record MerchantCreatedEvent : DomainEvent
{
    public required Guid MerchantId { get; init; }
    public required Guid OwnerId { get; init; }
    public required string Name { get; init; }
    public required string Slug { get; init; }
}
