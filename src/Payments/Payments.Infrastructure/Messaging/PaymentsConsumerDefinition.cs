using MassTransit;
using Haworks.BuildingBlocks.Messaging;

namespace Haworks.Payments.Infrastructure.Messaging;

/// <summary>
/// Per-bounded-context consumer definition that anchors a consumer's
/// receive endpoint to the payments DB outbox tables. Subclasses
/// <see cref="BoundedContextConsumerDefinition{TConsumer, TDbContext}"/>
/// from BuildingBlocks; mirrors the catalog/orders/etc pattern.
/// </summary>
public sealed class PaymentsConsumerDefinition<TConsumer>
    : BoundedContextConsumerDefinition<TConsumer, PaymentDbContext>
    where TConsumer : class, IConsumer
{
}
