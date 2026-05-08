namespace Haworks.Payments.Application.DTOs.Subscriptions;

public sealed record SubscriptionStatusDto(
    bool IsSubscribed,
    string? PlanId,
    DateTime? ExpiresAt);
