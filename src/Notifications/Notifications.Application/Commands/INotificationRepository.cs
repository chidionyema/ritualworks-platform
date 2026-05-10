using Haworks.Notifications.Domain.Entities;

namespace Haworks.Notifications.Application.Commands;

public interface INotificationRepository
{
    Task<Guid?> FindIdByIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct);

    void Add(Notification notification);

    Task<Notification?> GetByIdAsync(Guid id, CancellationToken ct);

    Task<Notification?> GetByProviderMessageIdAsync(string providerMessageId, CancellationToken ct);

    Task<int> SaveChangesAsync(CancellationToken ct);
}
