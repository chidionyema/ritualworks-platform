namespace Haworks.Contracts.Privacy;

public sealed record PrivacyErasureTimedOut : DomainEvent
{
    public required Guid RequestId { get; init; }
}
