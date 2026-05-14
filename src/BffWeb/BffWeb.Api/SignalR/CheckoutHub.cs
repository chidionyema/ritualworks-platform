using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Haworks.BffWeb.Api.SignalR;

/// <summary>
/// SignalR hub for pushing checkout state updates to the connected
/// browser. The flow:
///   1. Browser POSTs /api/checkout, gets back {sagaId, …}.
///   2. Browser opens a SignalR connection to /hubs/checkout and calls
///      <see cref="SubscribeToSaga"/> with the sagaId.
///   3. The hub adds the connection to a group named after the sagaId.
///   4. PaymentSessionCreatedConsumer (in the same process) consumes the
///      RabbitMQ event and calls <c>IHubContext&lt;CheckoutHub&gt;.Clients
///      .Group(sagaId).SendAsync("CheckoutReady", url)</c> — which lands
///      in the browser without polling.
///
/// The group naming convention (sagaId-as-string) is the only handshake
/// between the consumer and the hub — keeps the consumer free of
/// connection-tracking state.
/// </summary>
[Authorize]
public sealed class CheckoutHub : Hub
{
    /// <summary>Browser calls this with the sagaId returned from POST /api/checkout.</summary>
    public Task SubscribeToSaga(Guid sagaId) =>
        Groups.AddToGroupAsync(Context.ConnectionId, GroupNameFor(sagaId));

    public Task UnsubscribeFromSaga(Guid sagaId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupNameFor(sagaId));

    /// <summary>
    /// Stable group name — the consumer uses this exact form to push
    /// updates without needing a reference to the hub class.
    /// </summary>
    public static string GroupNameFor(Guid sagaId) => $"saga-{sagaId:N}";
}
