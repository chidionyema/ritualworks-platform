namespace Haworks.Contracts.Content;

/// <summary>
/// Fired when a content upload fails virus/signature validation and is quarantined.
/// Consumers: Notifications (alert owner), Audit (record quarantine).
/// </summary>
public sealed record ContentQuarantinedEvent : DomainEvent
{
    public required Guid ContentId { get; init; }
    public required string EntityId { get; init; }
    public required string EntityType { get; init; }
    public required string OwnerUserId { get; init; }
    public required string Reason { get; init; }
}
