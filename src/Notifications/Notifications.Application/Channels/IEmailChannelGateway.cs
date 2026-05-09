using Haworks.Notifications.Domain.Entities;

namespace Haworks.Notifications.Application.Channels;

public interface IEmailChannelGateway
{
    Task SendAsync(Notification notification, CancellationToken ct);
}
