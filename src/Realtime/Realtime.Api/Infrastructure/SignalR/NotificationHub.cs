using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Haworks.Realtime.Api.Application.Common;
using System.Security.Claims;

namespace Haworks.Realtime.Api.Infrastructure.SignalR;

/// <summary>
/// SignalR hub for real-time push notifications.
///
/// <para><b>At-least-once delivery (get-then-ack):</b> On connect, pending messages are fetched
/// from Redis without deletion. Only after all messages are successfully sent via SignalR is the
/// inbox acknowledged (deleted). If the SignalR send fails, messages remain in the inbox for the
/// next connection attempt.</para>
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

        var messages = await _inboxService.GetMessagesAsync(userId);
        if (messages.Count == 0)
        {
            await base.OnConnectedAsync();
            return;
        }

        // Send all messages; only ack after all succeed.
        foreach (var msg in messages)
        {
            _logger.LogInformation(
                "Pushing message to user. UserId={UserId}, MessageId={MessageId}, MessageType={MessageType}, Success={Success}",
                userId, msg.MessageId, msg.MessageType, true);

            await Clients.Caller.SendAsync("ReceiveNotification", msg);
        }

        // H2 Fix: Trim only the count we actually delivered (not DEL),
        // preserving any messages that arrived during the flush window.
        await _inboxService.AcknowledgeMessagesAsync(userId, messages.Count);
        await base.OnConnectedAsync();
    }
}
