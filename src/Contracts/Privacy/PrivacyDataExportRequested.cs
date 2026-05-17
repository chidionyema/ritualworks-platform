namespace Haworks.Contracts.Privacy;

public sealed record PrivacyDataExportRequested : DomainEvent
{
    public required Guid RequestId { get; init; }
    public required Guid UserId { get; init; }
}
