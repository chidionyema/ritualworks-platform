namespace Haworks.Notifications.Domain.ValueObjects;

public sealed record DeliveryAttempt(
    DateTime AttemptedAt,
    string ProviderName,
    string? ProviderMessageId,
    bool IsSuccess,
    string? ErrorMessage);
