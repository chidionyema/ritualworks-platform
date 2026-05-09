using Haworks.Notifications.Domain.Enums;

namespace Haworks.Notifications.Domain.Events;

/// <summary>
/// Raised on a hard bounce. Suppression-list consumer reacts by adding the
/// recipient hash + channel to the suppression list (per spec §8).
/// </summary>
public sealed record NotificationBouncedEvent
{
    public required Guid NotificationId { get; init; }
    public required string Recipient { get; init; }
    public required NotificationChannel Channel { get; init; }
    public required string Reason { get; init; }
    public Guid EventId { get; init; } = Guid.NewGuid();
    public DateTime OccurredAt { get; init; } = DateTime.UtcNow;
}
