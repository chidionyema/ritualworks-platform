using MassTransit;
using Haworks.BuildingBlocks.Messaging;
using Haworks.Payouts.Infrastructure.Persistence;

namespace Haworks.Payouts.Infrastructure.Messaging;

/// <summary>
/// Per-bounded-context consumer definition anchored to PayoutsDbContext's
/// outbox tables. Inbox dedup, business state writes, and downstream
/// publishes all commit atomically in one PayoutsDbContext transaction.
/// </summary>
public sealed class PayoutsConsumerDefinition<TConsumer>
    : BoundedContextConsumerDefinition<TConsumer, PayoutsDbContext>
    where TConsumer : class, IConsumer
{
}
