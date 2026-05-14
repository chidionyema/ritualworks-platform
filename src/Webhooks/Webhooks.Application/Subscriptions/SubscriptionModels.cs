namespace Haworks.Webhooks.Application.Subscriptions;

public sealed record WebhookSubscriptionDto(
    Guid Id,
    Guid PartnerId,
    string Url,
    string SecretPreview,
    string[] Events,
    bool IsActive,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record CreateWebhookSubscriptionRequest(
    Guid PartnerId,
    string Url,
    string[] Events,
    string? Secret,
    bool IsActive = true,
    string? Description = null);
