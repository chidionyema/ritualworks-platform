using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Haworks.BffWeb.Api.SignalR;

/// <summary>
/// SignalR hub for pushing checkout state updates to the connected browser.
///
/// Security: Group names embed the authenticated userId so a connection can only
/// subscribe to sagas it owns. The consumer uses the same naming convention
/// (userId + sagaId) to target the correct group.
/// </summary>
[Authorize]
public sealed class CheckoutHub : Hub
{
    /// <summary>
    /// Browser calls this with the sagaId returned from POST /api/checkout.
    /// The group name embeds the authenticated userId — prevents IDOR.
    /// </summary>
    public Task SubscribeToSaga(Guid sagaId)
    {
        var userId = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value
                     ?? Context.User?.FindFirst("sub")?.Value;

        if (string.IsNullOrEmpty(userId))
            throw new HubException("Authentication required.");

        return Groups.AddToGroupAsync(Context.ConnectionId, GroupNameFor(userId, sagaId));
    }

    public Task UnsubscribeFromSaga(Guid sagaId)
    {
        var userId = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value
                     ?? Context.User?.FindFirst("sub")?.Value;

        if (string.IsNullOrEmpty(userId))
            return Task.CompletedTask;

        return Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupNameFor(userId, sagaId));
    }

    /// <summary>
    /// Group name includes userId — consumer must know the userId to publish.
    /// This prevents IDOR: even if an attacker knows the sagaId, they can't
    /// subscribe because their userId produces a different group name.
    /// </summary>
    public static string GroupNameFor(string userId, Guid sagaId) => $"checkout-{userId}-{sagaId:N}";
}
