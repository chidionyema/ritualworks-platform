using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Haworks.Realtime.Api.Application.Common;
using System.Security.Claims;

namespace Haworks.Realtime.Api.Infrastructure.SignalR;

/// <summary>
/// SignalR hub for real-time push notifications.
///
/// <para><b>At-most-once delivery (inbox flush):</b> On connect, pending messages are fetched
/// from Redis and deleted atomically via <see cref="IInboxService.GetAndClearMessagesAsync"/>.
/// Each message is then forwarded to the caller over SignalR. If the SignalR send fails after
/// the Redis delete has already committed, those messages are permanently lost — they will not
/// be retried. This is an intentional trade-off: real-time notifications are best-effort and
/// transient. Consumers requiring guaranteed delivery must use a durable channel (e.g. polling
/// the notification REST API).</para>
/// </summary>
[Authorize]
public class NotificationHub : Hub
{
    private readonly IInboxService _inboxService;
    private readonly ILogger<NotificationHub> _logger;

    public NotificationHub(IInboxService inboxService, ILogger<NotificationHub> logger)
    {
        _inboxService = inboxService;
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        var userIdString = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(userIdString, out var userId))
        {
            _logger.LogWarning("NotificationHub: connection {ConnectionId} has a non-Guid or missing NameIdentifier claim (value: {Value}). Inbox flush skipped.", Context.ConnectionId, userIdString);
            await base.OnConnectedAsync();
            return;
        }

        _logger.LogInformation("User {UserId} connected. Flushing inbox.", userId);
        var messages = await _inboxService.GetAndClearMessagesAsync(userId);
        foreach (var msg in messages)
        {
            await Clients.Caller.SendAsync("ReceiveNotification", msg);
        }
        await base.OnConnectedAsync();
    }
}
