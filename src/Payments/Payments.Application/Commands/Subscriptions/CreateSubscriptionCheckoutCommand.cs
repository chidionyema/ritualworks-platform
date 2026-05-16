using MediatR;
using Haworks.Payments.Application.DTOs.Subscriptions;
using Haworks.Payments.Application.Interfaces;

namespace Haworks.Payments.Application.Commands.Subscriptions;

public sealed record CreateSubscriptionCheckoutCommand(
    string UserId,
    string PriceId,
    decimal Amount,
    string? RedirectPath) : IRequest<Result<CreateSubscriptionCheckoutResultDto>>;

internal sealed class CreateSubscriptionCheckoutCommandHandler(ISubscriptionService subscriptionService)
    : IRequestHandler<CreateSubscriptionCheckoutCommand, Result<CreateSubscriptionCheckoutResultDto>>
{
    public async Task<Result<CreateSubscriptionCheckoutResultDto>> Handle(CreateSubscriptionCheckoutCommand request, CancellationToken ct)
    {
        // Note: The PriceId here corresponds to the PlanId expected by the service.
        // The Amount is passed in the command but the underlying service uses the PlanId/PriceId
        // which typically determines the price in Stripe. We include it for consistency with the brief.
        
        var successUrl = $"{request.RedirectPath ?? "https://haworks.com"}/checkout/success?session_id={{CHECKOUT_SESSION_ID}}";
        var cancelUrl = $"{request.RedirectPath ?? "https://haworks.com"}/checkout/cancel";

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
