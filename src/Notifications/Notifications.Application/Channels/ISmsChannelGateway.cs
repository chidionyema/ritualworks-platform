using Haworks.Notifications.Domain.Entities;

namespace Haworks.Notifications.Application.Channels;

public interface ISmsChannelGateway
{
    Task SendAsync(Notification notification, CancellationToken ct);
}
