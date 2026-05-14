using Haworks.BuildingBlocks.Common;
using Haworks.Webhooks.Application.Subscriptions;
using MediatR;

namespace Haworks.Webhooks.Application.Subscriptions;

public sealed record CreateWebhookSubscriptionCommand(
    Guid PartnerId,
    string Url,
    string[] Events,
    string? Secret,
    bool IsActive) : IRequest<Result<Guid>>;

public sealed record UpdateWebhookSubscriptionCommand(
    Guid Id,
    string Url,
    string[] Events,
    bool IsActive) : IRequest<Result<WebhookSubscriptionDto>>;

public sealed record DeleteWebhookSubscriptionCommand(Guid Id) : IRequest<Result>;

public sealed record RotateWebhookSubscriptionSecretCommand(
    Guid Id,
    string? Secret) : IRequest<Result<string>>;

public sealed record GetWebhookSubscriptionQuery(Guid Id) : IRequest<Result<WebhookSubscriptionDto>>;
