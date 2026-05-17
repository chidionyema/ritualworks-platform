namespace Haworks.Contracts.Privacy;

public sealed record PrivacyDataExportCompleted : DomainEvent
{
    public required Guid RequestId { get; init; }
    public required Guid UserId { get; init; }
    public required string ServiceName { get; init; }
    public string? DataLink { get; init; }
}
