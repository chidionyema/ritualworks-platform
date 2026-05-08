using MediatR;
using Haworks.Payments.Application.DTOs.Subscriptions;
using Haworks.Payments.Application.Interfaces;

namespace Haworks.Payments.Application.Queries.Subscriptions;

public sealed record GetSubscriptionStatusQuery(string UserId) : IRequest<Result<SubscriptionStatusDto>>;

internal sealed class GetSubscriptionStatusQueryHandler(ISubscriptionManager subscriptionManager)
    : IRequestHandler<GetSubscriptionStatusQuery, Result<SubscriptionStatusDto>>
{
    public async Task<Result<SubscriptionStatusDto>> Handle(GetSubscriptionStatusQuery request, CancellationToken ct)
    {
        var result = await subscriptionManager.GetStatusAsync(request.UserId, ct);

        return Result.Success(new SubscriptionStatusDto(
            result.IsActive,
            result.PlanId,
            result.CurrentPeriodEnd));
    }
}
