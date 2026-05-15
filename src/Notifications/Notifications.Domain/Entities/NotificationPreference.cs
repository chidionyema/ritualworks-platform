using Haworks.BuildingBlocks.Persistence;
using Haworks.Notifications.Domain.Enums;

namespace Haworks.Notifications.Domain.Entities;

public sealed class NotificationPreference : AuditableEntity
{
    public string UserId { get; private set; } = string.Empty;
    public string Category { get; private set; } = string.Empty;
    public NotificationChannel Channel { get; private set; }
    public bool IsEnabled { get; private set; }
    public string? QuietHoursJson { get; private set; }

    private NotificationPreference() { }

    public static NotificationPreference Create(
        string userId,
        string category,
        NotificationChannel channel,
        bool isEnabled,
        string? quietHoursJson = null)
    {
        return new NotificationPreference
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Category = category,
            Channel = channel,
            IsEnabled = isEnabled,
            QuietHoursJson = quietHoursJson
        };
    }
}
