using Haworks.Payments.Application.Interfaces;
using Haworks.BuildingBlocks.Common;
using MediatR;

namespace Haworks.Payments.Application.Commands.Subscriptions;

public sealed record CancelSubscriptionCommand(
    string UserId, 
    string SubscriptionId, 
    bool Immediate = false) : IRequest<Result<bool>>;

public sealed class CancelSubscriptionCommandHandler(
    ISubscriptionManager subscriptionManager) : IRequestHandler<CancelSubscriptionCommand, Result<bool>>
{
    public async Task<Result<bool>> Handle(CancelSubscriptionCommand request, CancellationToken ct)
    {
        var success = await subscriptionManager.CancelAsync(request.SubscriptionId, request.Immediate, ct);
        return success ? Result<bool>.Success(true) : Result<bool>.Failure<bool>(Error.NotFound("Subscription.NotFound", "Subscription not found or cannot be cancelled."));
    }
}
