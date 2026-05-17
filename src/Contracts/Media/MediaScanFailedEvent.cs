namespace Haworks.Contracts.Media;

/// <summary>
/// Fired when virus scan fails and file is quarantined/rejected.
/// </summary>
public sealed record MediaScanFailedEvent : DomainEvent
{
    public required Guid MediaId { get; init; }
    public required string OwnerId { get; init; }
    public required string FileName { get; init; }
    public required string Reason { get; init; }
}
