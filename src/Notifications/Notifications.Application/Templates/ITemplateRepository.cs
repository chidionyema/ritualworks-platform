using Haworks.Notifications.Domain.Entities;
using Haworks.Notifications.Domain.Enums;

namespace Haworks.Notifications.Application.Templates;

public interface ITemplateRepository
{
    Task<NotificationTemplate?> GetAsync(string templateId, string locale, NotificationChannel channel, int version, CancellationToken ct = default);
    Task<IEnumerable<NotificationTemplate>> GetVersionsAsync(string templateId, CancellationToken ct = default);
    Task AddAsync(NotificationTemplate template, CancellationToken ct = default);
    Task UpdateAsync(NotificationTemplate template, CancellationToken ct = default);
}
