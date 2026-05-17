using MassTransit;
using Haworks.BuildingBlocks.Messaging;

namespace Haworks.Media.Api.Infrastructure.Messaging;

/// <summary>
/// Per-bounded-context consumer definition anchored to MediaDbContext's
/// outbox tables. Inbox dedup, business state writes, and downstream
/// publishes all commit atomically in one MediaDbContext transaction.
/// </summary>
public sealed class MediaConsumerDefinition<TConsumer>
    : BoundedContextConsumerDefinition<TConsumer, MediaDbContext>
    where TConsumer : class, IConsumer
{
}
