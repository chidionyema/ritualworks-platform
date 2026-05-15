using MassTransit;
using Haworks.BuildingBlocks.Messaging;
using Haworks.Payments.Domain;
using Haworks.Payments.Infrastructure.Persistence;

namespace Haworks.Payments.Infrastructure.Messaging;

/// <summary>
/// Definition for RefundSaga. Anchors to PaymentDbContext for
/// transactional outbox and inbox deduplication.
/// </summary>
public sealed class RefundSagaDefinition
    : BoundedContextSagaDefinition<RefundSagaState, PaymentDbContext>
{
}
