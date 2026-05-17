namespace Haworks.Contracts.Privacy;

public sealed record PrivacyErasureCompleted : DomainEvent
{
    public required Guid RequestId { get; init; }
    public required Guid UserId { get; init; }
    public required string ServiceName { get; init; }
}
