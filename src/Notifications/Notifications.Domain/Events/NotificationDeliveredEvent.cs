namespace Haworks.Notifications.Domain.Events;

/// <summary>
/// Raised when the provider's delivery webhook confirms recipient acceptance.
/// </summary>
public sealed record NotificationDeliveredEvent
{
    public required Guid NotificationId { get; init; }
    public required string ProviderMessageId { get; init; }
    public required DateTime DeliveredAt { get; init; }
    public Guid EventId { get; init; } = Guid.NewGuid();
    public DateTime OccurredAt { get; init; } = DateTime.UtcNow;
}
