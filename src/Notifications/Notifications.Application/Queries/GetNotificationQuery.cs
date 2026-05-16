using MediatR;
using Microsoft.Extensions.Logging;
using Haworks.Notifications.Application.Commands;
using Haworks.BuildingBlocks.Common;

namespace Haworks.Notifications.Application.Queries;

public sealed record GetNotificationQuery(Guid Id, string? RequestingUserId = null) : IRequest<Result<NotificationDto>>;

public sealed record NotificationDto(
    Guid Id,
    string Status,
    string? ErrorMessage,
    string Recipient,
    string Channel,
    string TemplateId,
    string Priority,
    string? UserId,
    DateTime? SentAt,
    DateTime? DeliveredAt,
    string? IdempotencyKey,
    IReadOnlyList<NotificationAttemptDto> Attempts);

public sealed record NotificationAttemptDto(
    DateTime AttemptedAt,
    string ProviderName,
    string? ProviderMessageId,
    bool IsSuccess,
    string? ErrorMessage);

internal sealed class GetNotificationQueryHandler(
    INotificationRepository repository,
    ILogger<GetNotificationQueryHandler> logger
) : IRequestHandler<GetNotificationQuery, Result<NotificationDto>>
{
    private static readonly Error NotFound = new(
        "Notifications.NotFound",
        "Notification not found.",
        ErrorType.NotFound);

    public async Task<Result<NotificationDto>> Handle(GetNotificationQuery request, CancellationToken ct)
    {
        var notification = await repository.GetByIdAsync(request.Id, ct).ConfigureAwait(false);
        if (notification is null)
        {
            logger.LogInformation("GetNotificationQuery: notification {NotificationId} not found", request.Id);
            return Result.Failure<NotificationDto>(NotFound);
        }

        // IDOR guard: return NotFound (not Forbidden) to avoid leaking existence
        if (request.RequestingUserId is not null &&
            !string.Equals(notification.UserId, request.RequestingUserId, StringComparison.Ordinal))
        {
            logger.LogWarning(
                "GetNotificationQuery: user {UserId} attempted access to notification {NotificationId} owned by {OwnerId}",
                request.RequestingUserId, request.Id, notification.UserId);
            return Result.Failure<NotificationDto>(NotFound);
        }

        var attempts = notification.DeliveryAttempts
            .Select(a => new NotificationAttemptDto(
                a.AttemptedAt,
                a.ProviderName,
                a.ProviderMessageId,
                a.IsSuccess,
                a.ErrorMessage))
            .ToArray();

        var dto = new NotificationDto(
            notification.Id,
            notification.Status.ToString(),
            notification.ErrorMessage,
            notification.Recipient,
            notification.Channel.ToString(),
            notification.TemplateId,
            notification.Priority.ToString(),
            notification.UserId,
            notification.SentAt,
            notification.DeliveredAt,
            notification.IdempotencyKey,
            attempts);

        return Result.Success(dto);
    }
}
