using Haworks.Notifications.Domain.Entities;
using Haworks.Notifications.Domain.Enums;

namespace Haworks.Notifications.Application.Templates;

public interface ITemplateSelector
{
    Task<NotificationTemplate> SelectAsync(string templateId, string locale, NotificationChannel channel, CancellationToken ct = default);
}
