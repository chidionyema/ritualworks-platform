using MassTransit;
using Haworks.BuildingBlocks.Messaging;

namespace Haworks.FeatureFlags.Api.Infrastructure.Messaging;

/// <summary>
/// Per-bounded-context consumer definition anchored to FeatureFlagsDbContext's
/// outbox tables. Inbox dedup, business state writes, and downstream
/// publishes all commit atomically in one FeatureFlagsDbContext transaction.
/// </summary>
public sealed class FeatureFlagsConsumerDefinition<TConsumer>
    : BoundedContextConsumerDefinition<TConsumer, FeatureFlagsDbContext>
    where TConsumer : class, IConsumer
{
}
