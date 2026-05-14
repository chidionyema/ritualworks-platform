using Hangfire;
using Haworks.BuildingBlocks.Common;
using Haworks.Webhooks.Application.Common;
using Haworks.Webhooks.Application.Interfaces;
using Haworks.Webhooks.Domain;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Haworks.Webhooks.Application.Deliveries;

public sealed record WebhookDeliveryDto(
    Guid Id,
    Guid SubscriptionId,
    string EventId,
    string EventType,
    string Payload,
    string Status,
    int Attempts,
    int? FinalStatus,
    DateTime CreatedAt,
    DateTime? CompletedAt);

public sealed record WebhookAttemptDto(
    Guid Id,
    int AttemptIndex,
    DateTime StartedAt,
    int? DurationMs,
    int? HttpStatus,
    string? ResponseBody,
    string? Error,
    bool Succeeded);

public sealed record GetDeliveriesQuery(
    Guid? SubscriptionId = null,
    string? EventType = null,
    string? Status = null,
    int Skip = 0,
    int Take = 50) : IRequest<Result<PagedResult<WebhookDeliveryDto>>>;

public sealed record GetDeliveryAttemptsQuery(Guid DeliveryId) : IRequest<Result<IReadOnlyList<WebhookAttemptDto>>>;

public sealed record ReplayDeliveryCommand(Guid DeliveryId) : IRequest<Result<Guid>>;

internal sealed class DeliveryHandlers(
    IWebhooksDbContext db,
    IBackgroundJobClient jobClient) :
    IRequestHandler<GetDeliveriesQuery, Result<PagedResult<WebhookDeliveryDto>>>,
    IRequestHandler<GetDeliveryAttemptsQuery, Result<IReadOnlyList<WebhookAttemptDto>>>,
    IRequestHandler<ReplayDeliveryCommand, Result<Guid>>
{
    public async Task<Result<PagedResult<WebhookDeliveryDto>>> Handle(GetDeliveriesQuery request, CancellationToken ct)
    {
        var query = db.Deliveries.AsNoTracking().AsQueryable();

        if (request.SubscriptionId.HasValue)
            query = query.Where(d => d.SubscriptionId == request.SubscriptionId.Value);

        if (!string.IsNullOrEmpty(request.EventType))
            query = query.Where(d => d.EventType == request.EventType);

        if (!string.IsNullOrEmpty(request.Status) && Enum.TryParse<DeliveryStatus>(request.Status, true, out var status))
            query = query.Where(d => d.Status == status);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(d => d.CreatedAt)
            .Skip(request.Skip)
            .Take(request.Take)
            .Select(d => Map(d))
            .ToListAsync(ct);

        return Result.Success(new PagedResult<WebhookDeliveryDto>(items, total, request.Skip, request.Take));
    }

    public async Task<Result<IReadOnlyList<WebhookAttemptDto>>> Handle(GetDeliveryAttemptsQuery request, CancellationToken ct)
    {
        var attempts = await db.DeliveryAttempts
            .AsNoTracking()
            .Where(a => a.DeliveryId == request.DeliveryId)
            .OrderBy(a => a.AttemptIndex)
            .Select(a => MapAttempt(a))
            .ToListAsync(ct);

        return Result.Success<IReadOnlyList<WebhookAttemptDto>>(attempts);
    }

    public async Task<Result<Guid>> Handle(ReplayDeliveryCommand request, CancellationToken ct)
    {
        var original = await db.Deliveries.AsNoTracking().FirstOrDefaultAsync(d => d.Id == request.DeliveryId, ct);
        if (original == null) return Result.Failure<Guid>(Error.NotFound("Webhook.DeliveryNotFound", "Webhook delivery not found."));

        // Replay per spec §5.4: creates a new delivery with same payload
        var replay = new WebhookDelivery(original.SubscriptionId, original.EventId, original.EventType, original.Payload);
        db.Deliveries.Add(replay);
        await db.SaveChangesAsync(ct);

        jobClient.Enqueue<IWebhookDispatcher>(x => x.DispatchAsync(replay.Id, CancellationToken.None));

        return Result.Success(replay.Id);
    }

    private static WebhookDeliveryDto Map(WebhookDelivery d) => new(
        d.Id, d.SubscriptionId, d.EventId, d.EventType, d.Payload, d.Status.ToString(), d.Attempts, d.FinalStatus, d.CreatedAt, d.CompletedAt);

    private static WebhookAttemptDto MapAttempt(WebhookDeliveryAttempt a) => new(
        a.Id, a.AttemptIndex, a.StartedAt, a.DurationMs, a.HttpStatus, a.ResponseBody, a.Error, a.Succeeded);
}
