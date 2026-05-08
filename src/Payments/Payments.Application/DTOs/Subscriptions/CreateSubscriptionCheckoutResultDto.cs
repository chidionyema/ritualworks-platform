namespace Haworks.Payments.Application.DTOs.Subscriptions;

public sealed record CreateSubscriptionCheckoutResultDto(
    string SessionId,
    string CheckoutUrl);
