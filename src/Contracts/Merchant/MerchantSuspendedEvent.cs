namespace Haworks.Contracts.Merchant;

public sealed record MerchantSuspendedEvent : DomainEvent
{
    public required Guid MerchantId { get; init; }
}
