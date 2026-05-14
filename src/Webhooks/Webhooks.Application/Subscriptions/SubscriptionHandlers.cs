using Haworks.BuildingBlocks.Common;
using Haworks.Webhooks.Domain;
using Haworks.Webhooks.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;

namespace Haworks.Webhooks.Application.Subscriptions;

internal sealed class SubscriptionHandlers(
    IWebhooksDbContext db,
    IHttpClientFactory httpFactory,
    ILogger<SubscriptionHandlers> logger) :
    IRequestHandler<CreateWebhookSubscriptionCommand, Result<Guid>>,
    IRequestHandler<UpdateWebhookSubscriptionCommand, Result<WebhookSubscriptionDto>>,
    IRequestHandler<DeleteWebhookSubscriptionCommand, Result>,
    IRequestHandler<RotateWebhookSubscriptionSecretCommand, Result<string>>,
    IRequestHandler<GetWebhookSubscriptionQuery, Result<WebhookSubscriptionDto>>
{
    private static readonly Error SubscriptionNotFound = Error.NotFound("Webhook.SubscriptionNotFound", "Webhook subscription not found.");

    public async Task<Result<Guid>> Handle(CreateWebhookSubscriptionCommand request, CancellationToken ct)
    {
        var isValid = await ValidateWebhookUrlAsync(request.Url, ct);
        if (!isValid)
        {
            return Result.Failure<Guid>(Error.Validation("Webhooks.InvalidUrl", "The webhook URL did not return a successful response during validation."));
        }

        var secret = request.Secret ?? Guid.NewGuid().ToString("N");
        var secretHash = BCrypt.Net.BCrypt.HashPassword(secret);
        var secretPreview = secret.Length > 4 ? secret[^4..] : secret;

        var sub = new WebhookSubscription(
            request.PartnerId,
            request.Url,
            secret,
            secretHash,
            secretPreview,
            request.Events,
            request.IsActive);

        db.Subscriptions.Add(sub);
        await db.SaveChangesAsync(ct);

        return Result.Success(sub.Id);
    }

    public async Task<Result<WebhookSubscriptionDto>> Handle(UpdateWebhookSubscriptionCommand request, CancellationToken ct)
    {
        var sub = await db.Subscriptions.FirstOrDefaultAsync(s => s.Id == request.Id && s.PartnerId == request.CallerPartnerId && s.DeletedAt == null, ct);
        if (sub == null) return Result.Failure<WebhookSubscriptionDto>(SubscriptionNotFound);

        sub.Update(request.Url, request.Events, request.IsActive);
        await db.SaveChangesAsync(ct);

        return Result.Success(Map(sub));
    }

    public async Task<Result> Handle(DeleteWebhookSubscriptionCommand request, CancellationToken ct)
    {
        var sub = await db.Subscriptions.FirstOrDefaultAsync(s => s.Id == request.Id && s.PartnerId == request.CallerPartnerId && s.DeletedAt == null, ct);
        if (sub == null) return Result.Failure(SubscriptionNotFound);

        sub.SoftDelete();
        await db.SaveChangesAsync(ct);

        return Result.Success();
    }

    public async Task<Result<string>> Handle(RotateWebhookSubscriptionSecretCommand request, CancellationToken ct)
    {
        var sub = await db.Subscriptions.FirstOrDefaultAsync(s => s.Id == request.Id && s.PartnerId == request.CallerPartnerId && s.DeletedAt == null, ct);
        if (sub == null) return Result.Failure<string>(SubscriptionNotFound);

        var secret = request.Secret ?? Guid.NewGuid().ToString("N");
        var secretHash = BCrypt.Net.BCrypt.HashPassword(secret);
        var secretPreview = secret.Length > 4 ? secret[^4..] : secret;

        sub.RotateSecret(secret, secretHash, secretPreview);
        await db.SaveChangesAsync(ct);

        return Result.Success(secret);
    }

    public async Task<Result<WebhookSubscriptionDto>> Handle(GetWebhookSubscriptionQuery request, CancellationToken ct)
    {
        var sub = await db.Subscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == request.Id && s.PartnerId == request.CallerPartnerId && s.DeletedAt == null, ct);
        if (sub == null) return Result.Failure<WebhookSubscriptionDto>(SubscriptionNotFound);

        return Result.Success(Map(sub));
    }

    private async Task<bool> ValidateWebhookUrlAsync(string url, CancellationToken ct)
    {
        try
        {
            var client = httpFactory.CreateClient("WebhookValidator");
            client.Timeout = TimeSpan.FromSeconds(5);
            var response = await client.PostAsJsonAsync(url, new { @event = "webhook.test", data = new { } }, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Webhook URL validation failed for {Url}", url);
            return false;
        }
    }

    private static WebhookSubscriptionDto Map(WebhookSubscription s) => new(
        s.Id,
        s.PartnerId,
        s.Url,
        s.SecretPreview,
        s.Events,
        s.IsActive,
        s.CreatedAt,
        s.LastModifiedDate ?? s.CreatedAt);
}
