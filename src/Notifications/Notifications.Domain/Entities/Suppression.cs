using Haworks.BuildingBlocks.Persistence;
using Haworks.Notifications.Domain.Enums;

namespace Haworks.Notifications.Domain.Entities;

public sealed class Suppression : AuditableEntity
{
    public string RecipientHash { get; private set; } = string.Empty;
    public NotificationChannel Channel { get; private set; }
    public string Reason { get; private set; } = string.Empty;
    public string? SourceEventId { get; private set; }

    private Suppression() { }

    public static Suppression Create(string recipientHash, NotificationChannel channel, string reason, string? sourceEventId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipientHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new Suppression
        {
            RecipientHash = recipientHash,
            Channel = channel,
            Reason = reason,
            SourceEventId = sourceEventId
        };
    }
}
