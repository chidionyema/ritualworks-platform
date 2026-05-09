using MassTransit;

namespace Haworks.Notifications.Application.Consumers;

public sealed class NotificationRequestConsumer : IConsumer<object>
{
    public Task Consume(ConsumeContext<object> context)
        => throw new NotImplementedException("Track L3 owns this body");
}
