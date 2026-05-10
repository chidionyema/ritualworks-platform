using Microsoft.EntityFrameworkCore;
using Haworks.Notifications.Domain.Entities;
using Haworks.Notifications.Application.Commands;

namespace Haworks.Notifications.Infrastructure.Persistence;

internal sealed class NotificationRepository(NotificationsDbContext dbContext) : INotificationRepository
{
    public async Task<Guid?> FindIdByIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct)
    {
        return await dbContext.Notifications
            .AsNoTracking()
            .Where(n => n.IdempotencyKey == idempotencyKey)
            .Select(n => (Guid?)n.Id)
            .FirstOrDefaultAsync(ct);
    }

    public void Add(Notification notification)
    {
        dbContext.Notifications.Add(notification);
    }

    public async Task<Notification?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        return await dbContext.Notifications
            .Include(n => n.DeliveryAttempts)
            .FirstOrDefaultAsync(n => n.Id == id, ct);
    }

    public async Task<Notification?> GetByProviderMessageIdAsync(string providerMessageId, CancellationToken ct)
    {
        return await dbContext.Notifications
            .Include(n => n.DeliveryAttempts)
            .FirstOrDefaultAsync(n => n.ProviderMessageId == providerMessageId, ct);
    }

    public async Task<int> SaveChangesAsync(CancellationToken ct)
    {
        return await dbContext.SaveChangesAsync(ct);
    }
}
