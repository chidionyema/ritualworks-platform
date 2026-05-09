using Haworks.Notifications.Domain.Entities;
using Haworks.Notifications.Domain.Enums;

namespace Haworks.Notifications.Application.Suppression;

public interface ISuppressionRepository
{
    Task<bool> ExistsAsync(string recipientHash, NotificationChannel channel);
    Task AddAsync(Domain.Entities.Suppression suppression);
}
