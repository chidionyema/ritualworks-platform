namespace Haworks.Notifications.Domain.Enums;

public enum NotificationStatus
{
    Created,
    Rendering,
    Queued,
    Sent,
    Delivered,
    Bounced,
    Complained,
    Failed,
    Suppressed
}
