namespace Haworks.Notifications.Domain.Events;

/// <summary>
/// Raised when all providers in a channel are exhausted (or an unrecoverable
/// pre-queue failure occurs). Operations alerting + DLQ inspection consume this.
/// </summary>
public sealed record NotificationFailedEvent
{
    public required Guid NotificationId { get; init; }
    public required string Reason { get; init; }
    public int AttemptsMade { get; init; }
    public Guid EventId { get; init; } = Guid.NewGuid();
    public DateTime OccurredAt { get; init; } = DateTime.UtcNow;
}
