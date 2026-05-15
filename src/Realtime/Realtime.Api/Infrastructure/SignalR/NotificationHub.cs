using Microsoft.AspNetCore.SignalR;
using Haworks.Realtime.Api.Application.Common;
using System.Security.Claims;

namespace Haworks.Realtime.Api.Infrastructure.SignalR;

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
        if (Guid.TryParse(userIdString, out var userId))
        {
            _logger.LogInformation("User {UserId} connected. Flushing inbox.", userId);
            var messages = await _inboxService.GetAndClearMessagesAsync(userId);
            foreach (var msg in messages)
            {
                await Clients.Caller.SendAsync("ReceiveNotification", msg);
            }
        }
        await base.OnConnectedAsync();
    }
}
