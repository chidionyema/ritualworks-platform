using Haworks.Payments.Application.Interfaces;
using Haworks.BuildingBlocks.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Haworks.Payments.Application.Commands.Subscriptions;

public sealed record CancelSubscriptionCommand(
    string UserId,
    string SubscriptionId,
    bool Immediate = false) : IRequest<Result<bool>>;

public sealed class CancelSubscriptionCommandHandler(
    ISubscriptionManager subscriptionManager,
    IPaymentDbContext db) : IRequestHandler<CancelSubscriptionCommand, Result<bool>>
{
    public async Task<Result<bool>> Handle(CancelSubscriptionCommand request, CancellationToken ct)
    {
        var subscription = await db.Subscriptions
            .FirstOrDefaultAsync(s => s.ProviderSubscriptionId == request.SubscriptionId, ct);

        if (subscription is null)
            return Result<bool>.Failure<bool>(Error.NotFound("Subscription.NotFound", "Subscription not found or cannot be cancelled."));

        if (subscription.UserId != request.UserId)
            return Result<bool>.Failure<bool>(Error.Forbidden("Subscription.Forbidden", "You do not own this subscription."));

        var success = await subscriptionManager.CancelAsync(request.SubscriptionId, request.Immediate, ct);
        return success ? Result<bool>.Success(true) : Result<bool>.Failure<bool>(Error.NotFound("Subscription.NotFound", "Subscription not found or cannot be cancelled."));
    }
}
