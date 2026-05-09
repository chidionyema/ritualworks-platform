using Haworks.Notifications.Domain.Enums;

namespace Haworks.Notifications.Domain.Events;

/// <summary>
/// Raised when a Notification aggregate enters <see cref="NotificationStatus.Created"/>.
/// Domain-internal event (no Contracts reference) — Application layer translates this
/// into the cross-service integration event (e.g. NotificationAcceptedEvent in Contracts)
/// before publishing through the outbox.
/// </summary>
public sealed record NotificationCreatedEvent
{
    public required Guid NotificationId { get; init; }
    public required string Recipient { get; init; }
    public required NotificationChannel Channel { get; init; }
    public required string TemplateId { get; init; }
    public required string IdempotencyKey { get; init; }
    public string? UserId { get; init; }
    public NotificationPriority Priority { get; init; } = NotificationPriority.Normal;
    public Guid EventId { get; init; } = Guid.NewGuid();
    public DateTime OccurredAt { get; init; } = DateTime.UtcNow;
}
