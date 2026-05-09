using Haworks.Notifications.Domain.Enums;

namespace Haworks.Notifications.Application.Suppression;

public interface ISuppressionService
{
    Task<bool> IsSuppressedAsync(string recipient, NotificationChannel channel, CancellationToken ct);
    Task AddAsync(string recipient, NotificationChannel channel, string reason, string? sourceEventId, CancellationToken ct);
}
