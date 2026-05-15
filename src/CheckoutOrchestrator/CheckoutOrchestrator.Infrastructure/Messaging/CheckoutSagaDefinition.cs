using MassTransit;
using Haworks.BuildingBlocks.Messaging;
using Haworks.CheckoutOrchestrator.Domain;
using Haworks.CheckoutOrchestrator.Infrastructure.Persistence;

namespace Haworks.CheckoutOrchestrator.Infrastructure.Messaging;

/// <summary>
/// Definition for CheckoutSaga. Anchors the saga to CheckoutDbContext's
/// outbox/inbox tables to ensure transactional integrity and deduplication.
/// </summary>
public sealed class CheckoutSagaDefinition
    : BoundedContextSagaDefinition<CheckoutSagaState, CheckoutDbContext>
{
}
