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

    public static Suppression Create() => throw new NotImplementedException("Track L1.D owns this body");
}
