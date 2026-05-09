using Haworks.Notifications.Domain.Entities;
using Haworks.Notifications.Domain.Enums;

namespace Haworks.Notifications.Application.Templates;

public interface ITemplateRepository
{
    Task<NotificationTemplate?> GetAsync(string templateId, string locale, NotificationChannel channel, int version);
    Task<IEnumerable<NotificationTemplate>> GetVersionsAsync(string templateId);
    Task AddAsync(NotificationTemplate template);
    Task UpdateAsync(NotificationTemplate template);
}
