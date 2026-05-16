using MediatR;
using Microsoft.Extensions.Options;
using Haworks.BuildingBlocks.Common;
using Haworks.Payments.Application.DTOs.Subscriptions;
using Haworks.Payments.Application.Interfaces;

namespace Haworks.Payments.Application.Commands.Subscriptions;

public sealed record CreateSubscriptionCheckoutCommand(
    string UserId,
    string PriceId,
    decimal Amount,
    string? RedirectPath) : IRequest<Result<CreateSubscriptionCheckoutResultDto>>;

internal sealed class CreateSubscriptionCheckoutCommandHandler(
    ISubscriptionService subscriptionService,
    IOptions<BrandOptions> brandOptions)
    : IRequestHandler<CreateSubscriptionCheckoutCommand, Result<CreateSubscriptionCheckoutResultDto>>
{
    public async Task<Result<CreateSubscriptionCheckoutResultDto>> Handle(CreateSubscriptionCheckoutCommand request, CancellationToken ct)
    {
        var baseUrl = request.RedirectPath ?? brandOptions.Value.PrimaryUrl.TrimEnd('/');
        var successUrl = $"{baseUrl}/checkout/success?session_id={{CHECKOUT_SESSION_ID}}";
        var cancelUrl = $"{baseUrl}/checkout/cancel";

        var sessionRequest = new CreateSubscriptionSessionRequest(
            request.UserId,
            string.Empty, // Email will be resolved by the underlying service or from the token
            request.PriceId,
            successUrl,
            cancelUrl,
            Guid.NewGuid().ToString("N")); // Idempotency key

        var result = await subscriptionService.CreateSubscriptionSessionAsync(sessionRequest, ct);

        return Result.Success(new CreateSubscriptionCheckoutResultDto(
            result.SessionId,
            result.SessionUrl));
    }
}
