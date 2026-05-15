using MassTransit;
using Haworks.BuildingBlocks.Messaging;
using Haworks.Privacy.Application.Requests.Sagas;
using Haworks.Privacy.Infrastructure.Persistence;

namespace Haworks.Privacy.Infrastructure.Messaging;

/// <summary>
/// Definition for PrivacyRequestSaga. Anchors to PrivacyDbContext for
/// transactional outbox and inbox deduplication.
/// </summary>
public sealed class PrivacyRequestSagaDefinition
    : BoundedContextSagaDefinition<PrivacyRequestState, PrivacyDbContext>
{
}
