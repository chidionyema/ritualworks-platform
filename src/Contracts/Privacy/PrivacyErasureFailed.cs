namespace Haworks.Contracts.Privacy;

public sealed record PrivacyErasureFailed : DomainEvent
{
    public required Guid RequestId { get; init; }
    public required Guid UserId { get; init; }
    public required string ServiceName { get; init; }
    public required string ErrorMessage { get; init; }
}
