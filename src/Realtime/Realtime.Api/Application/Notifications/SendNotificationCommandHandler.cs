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
        var messageId = Guid.NewGuid();
        var success = false;

        try
        {
            // Store in inbox for offline/reconnect support
            await _inboxService.StoreMessageAsync(request.UserId, new { request.MessageType, request.Data }, cancellationToken);

            // Send via SignalR
            await _hubContext.Clients.User(request.UserId.ToString())
                .SendAsync("ReceiveNotification", new { MessageId = messageId, request.MessageType, request.Data }, cancellationToken);

            success = true;
            return Result<Unit>.Success(Unit.Value);
        }
        finally
        {
            _logger.LogInformation(
                "Notification pushed. UserId={UserId}, MessageType={MessageType}, MessageId={MessageId}, Success={Success}",
                request.UserId, request.MessageType, messageId, success);
        }
    }
}
