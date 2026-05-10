namespace Haworks.Notifications.Application.Webhooks;

public sealed record NotificationWebhookValidatedEvent
{
    public string Provider { get; init; } = string.Empty;
    public string ProviderEventId { get; init; } = string.Empty;
    public string EventType { get; init; } = string.Empty;
    public string RawPayload { get; init; } = string.Empty;
    public string? Signature { get; init; }
}
