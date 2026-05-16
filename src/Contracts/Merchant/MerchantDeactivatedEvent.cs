namespace Haworks.Contracts.Merchant;

public sealed record MerchantDeactivatedEvent : DomainEvent
{
    public required Guid MerchantId { get; init; }
}
