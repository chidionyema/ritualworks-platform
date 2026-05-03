using MassTransit;
using Haworks.BuildingBlocks.Messaging;

namespace Haworks.Orders.Infrastructure.Messaging;

/// <summary>
/// Per-bounded-context consumer definition anchored to OrderDbContext's
/// outbox tables. Inbox dedup, business state writes, and downstream
/// publishes all commit atomically in one OrderDbContext transaction.
/// </summary>
public sealed class OrdersConsumerDefinition<TConsumer>
    : BoundedContextConsumerDefinition<TConsumer, OrderDbContext>
    where TConsumer : class, IConsumer
{
}
