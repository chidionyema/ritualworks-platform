using Haworks.Payments.Application.Interfaces;
using Haworks.BuildingBlocks.Common;
using MediatR;

namespace Haworks.Payments.Application.Commands.Subscriptions;

public sealed record ResumeSubscriptionCommand(
    string UserId, 
    string SubscriptionId) : IRequest<Result<bool>>;

public sealed class ResumeSubscriptionCommandHandler(
    ISubscriptionManager subscriptionManager) : IRequestHandler<ResumeSubscriptionCommand, Result<bool>>
{
    public async Task<Result<bool>> Handle(ResumeSubscriptionCommand request, CancellationToken ct)
    {
        var success = await subscriptionManager.ResumeAsync(request.SubscriptionId, ct);
        return success ? Result<bool>.Success(true) : Result<bool>.Failure<bool>(Error.NotFound("Subscription.NotFound", "Subscription not found or cannot be resumed."));
    }
}
