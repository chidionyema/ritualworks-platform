using MassTransit;
using Haworks.BuildingBlocks.Messaging;

namespace Haworks.Catalog.Infrastructure.Messaging;

/// <summary>
/// Per-bounded-context consumer definition anchored to CatalogDbContext's
/// outbox tables. Mirrors the orders/payments pattern.
/// </summary>
public sealed class CatalogConsumerDefinition<TConsumer>
    : BoundedContextConsumerDefinition<TConsumer, CatalogDbContext>
    where TConsumer : class, IConsumer
{
}
