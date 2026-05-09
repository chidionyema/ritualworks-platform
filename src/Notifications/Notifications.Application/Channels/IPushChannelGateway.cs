using Haworks.Notifications.Domain.Entities;

namespace Haworks.Notifications.Application.Channels;

public interface IPushChannelGateway
{
    Task SendAsync(Notification notification, CancellationToken ct);
}
