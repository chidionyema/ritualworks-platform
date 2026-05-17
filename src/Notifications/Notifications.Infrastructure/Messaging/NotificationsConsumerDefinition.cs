using MassTransit;
using Haworks.BuildingBlocks.Messaging;
using Haworks.Notifications.Infrastructure.Persistence;

namespace Haworks.Notifications.Infrastructure.Messaging;

/// <summary>
/// Per-bounded-context consumer definition anchored to NotificationsDbContext's
/// outbox tables. Inbox dedup, business state writes, and downstream
/// publishes all commit atomically in one NotificationsDbContext transaction.
/// </summary>
public sealed class NotificationsConsumerDefinition<TConsumer>
    : BoundedContextConsumerDefinition<TConsumer, NotificationsDbContext>
    where TConsumer : class, IConsumer
{
}
