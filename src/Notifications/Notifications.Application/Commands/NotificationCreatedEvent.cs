using Haworks.Notifications.Domain.Enums;

namespace Haworks.Notifications.Application.Commands;

/// <summary>
/// Cross-context integration event raised when a Notification row is persisted
/// in the <c>Created</c> state and is ready for downstream rendering/dispatch.
///
/// Lives in <c>Application/Commands</c> rather than the shared
/// <c>Haworks.Contracts</c> project because L1.G owns this track and the
/// Contracts project sits outside our owned paths. A future track may
/// promote this record to <c>Haworks.Contracts.Notifications</c> when
/// downstream consumers (e.g. the channel dispatcher in L3) need a stable
/// binding.
/// </summary>
public sealed record NotificationCreatedEvent
{
    public required Guid NotificationId { get; init; }
    public required string TemplateId { get; init; }
    public required NotificationChannel Channel { get; init; }
    public required NotificationPriority Priority { get; init; }
    public string? UserId { get; init; }
    public required string Recipient { get; init; }
    public required string IdempotencyKey { get; init; }
    public DateTime OccurredAt { get; init; } = DateTime.UtcNow;
}
