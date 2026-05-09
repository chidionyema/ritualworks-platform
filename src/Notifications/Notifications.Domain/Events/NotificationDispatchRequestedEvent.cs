using Haworks.Notifications.Domain.Enums;

namespace Haworks.Notifications.Domain.Events;

/// <summary>
/// Raised when a rendered Notification has been queued for a provider call.
/// Signals the channel-dispatch worker to pick the message up.
/// </summary>
public sealed record NotificationDispatchRequestedEvent
{
    public required Guid NotificationId { get; init; }
    public required NotificationChannel Channel { get; init; }
    public required NotificationPriority Priority { get; init; }
    public required string Recipient { get; init; }
    public Guid EventId { get; init; } = Guid.NewGuid();
    public DateTime OccurredAt { get; init; } = DateTime.UtcNow;
}
