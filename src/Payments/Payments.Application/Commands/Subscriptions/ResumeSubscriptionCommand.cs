using Haworks.Payments.Application.Interfaces;
using Haworks.BuildingBlocks.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Haworks.Payments.Application.Commands.Subscriptions;

public sealed record ResumeSubscriptionCommand(
    string UserId,
    string SubscriptionId,
    string IdempotencyKey) : IRequest<Result<bool>>;

public sealed class ResumeSubscriptionCommandHandler(
    ISubscriptionManager subscriptionManager,
    IPaymentDbContext db) : IRequestHandler<ResumeSubscriptionCommand, Result<bool>>
{
    public async Task<Result<bool>> Handle(ResumeSubscriptionCommand request, CancellationToken ct)
    {
        var subscription = await db.Subscriptions
            .FirstOrDefaultAsync(s => s.ProviderSubscriptionId == request.SubscriptionId, ct);

        if (subscription is null)
            return Result<bool>.Failure<bool>(Error.NotFound("Subscription.NotFound", "Subscription not found or cannot be resumed."));

        if (!string.Equals(subscription.UserId, request.UserId, StringComparison.Ordinal))
            return Result<bool>.Failure<bool>(Error.Forbidden("Subscription.Forbidden", "You do not own this subscription."));

        var success = await subscriptionManager.ResumeAsync(request.SubscriptionId, ct);
        return success ? Result<bool>.Success(true) : Result<bool>.Failure<bool>(Error.NotFound("Subscription.NotFound", "Subscription not found or cannot be resumed."));
    }
}
