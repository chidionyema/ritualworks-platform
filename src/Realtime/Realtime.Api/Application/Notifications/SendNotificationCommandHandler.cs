using Haworks.BuildingBlocks.Common;
using Haworks.Realtime.Api.Application.Common;
using Haworks.Realtime.Api.Infrastructure.SignalR;
using MediatR;
using Microsoft.AspNetCore.SignalR;

namespace Haworks.Realtime.Api.Application.Notifications;

public class SendNotificationCommandHandler : IRequestHandler<SendNotificationCommand, Result<Unit>>
{
    private readonly IHubContext<NotificationHub> _hubContext;
    private readonly IInboxService _inboxService;
    private readonly ILogger<SendNotificationCommandHandler> _logger;

    public SendNotificationCommandHandler(
        IHubContext<NotificationHub> hubContext,
        IInboxService inboxService,
        ILogger<SendNotificationCommandHandler> logger)
    {
        _hubContext = hubContext;
        _inboxService = inboxService;
        _logger = logger;
    }

    public async Task<Result<Unit>> Handle(SendNotificationCommand request, CancellationToken cancellationToken)
    {
        // H1 Fix: Single messageId shared between inbox and SignalR push for client-side dedup
        var messageId = Guid.NewGuid();

        // C1+C2 Fix: Pass explicit messageId and messageType to inbox (dedup + correct type)
        await _inboxService.StoreMessageAsync(
            request.UserId, messageId, request.MessageType, request.Data, cancellationToken);

        await _hubContext.Clients.User(request.UserId.ToString())
            .SendAsync("ReceiveNotification",
                new { MessageId = messageId, request.MessageType, request.Data },
                cancellationToken);

        _logger.LogInformation(
            "Notification pushed. UserId={UserId}, MessageType={MessageType}, MessageId={MessageId}",
            request.UserId, request.MessageType, messageId);

        return Result<Unit>.Success(Unit.Value);
    }
}
