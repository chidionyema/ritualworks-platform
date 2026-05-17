namespace Haworks.Contracts.Privacy;

public sealed record InitiatePrivacyRequestMessage : DomainEvent
{
    public required Guid RequestId { get; init; }
    public required Guid UserId { get; init; }
}
