using Haworks.BuildingBlocks.Persistence;
using Haworks.Notifications.Domain.Enums;
using Haworks.Notifications.Domain.ValueObjects;

namespace Haworks.Notifications.Domain.Entities;

public sealed class Notification : AuditableEntity
{
    private readonly List<DeliveryAttempt> _deliveryAttempts = new();

    public string? UserId { get; private set; }
    public string Recipient { get; private set; } = string.Empty;
    public NotificationChannel Channel { get; private set; }
    public string TemplateId { get; private set; } = string.Empty;
    public NotificationStatus Status { get; private set; }
    public NotificationPriority Priority { get; private set; }
    public string Subject { get; private set; } = string.Empty;
    public string Body { get; private set; } = string.Empty;
    public string? ErrorMessage { get; private set; }
    public DateTime? SentAt { get; private set; }
    public DateTime? DeliveredAt { get; private set; }
    public string? IdempotencyKey { get; private set; }
    public IReadOnlyCollection<DeliveryAttempt> DeliveryAttempts => _deliveryAttempts.AsReadOnly();

    private Notification() { }

    public static Notification Create() => throw new NotImplementedException("Track L1.A owns this body");
    public void MarkRendering() => throw new NotImplementedException("Track L1.A owns this body");
    public void MarkQueued() => throw new NotImplementedException("Track L1.A owns this body");
    public void MarkSent() => throw new NotImplementedException("Track L1.A owns this body");
    public void MarkDelivered() => throw new NotImplementedException("Track L1.A owns this body");
    public void MarkBounced(string reason) => throw new NotImplementedException("Track L1.A owns this body");
    public void MarkComplained() => throw new NotImplementedException("Track L1.A owns this body");
    public void MarkFailed(string reason) => throw new NotImplementedException("Track L1.A owns this body");
    public void RecordAttempt(DeliveryAttempt attempt) => throw new NotImplementedException("Track L1.A owns this body");
}
