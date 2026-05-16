namespace Haworks.Contracts.Merchant;

public sealed record MerchantActivatedEvent : DomainEvent
{
    public required Guid MerchantId { get; init; }
}
