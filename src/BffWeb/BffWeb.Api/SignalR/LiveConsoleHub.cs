using Haworks.BffWeb.Api.Demo;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Haworks.BffWeb.Api.SignalR;

/// <summary>
/// Public, anonymous hub that streams a live tail of every <c>/api/*</c>
/// request handled by this BFF process. Powers the visitor-facing
/// "live cluster activity" dock on the portfolio site — the artifact that
/// proves the demos are wired to a real backend rather than a mocked
/// frontend.
///
/// On connect, the hub backfills the caller with the last ~200 events so
/// the dock is populated immediately even if the visitor hasn't pressed a
/// button yet. After that, every new event arrives via <c>OnConsoleEvent</c>.
/// </summary>
[AllowAnonymous]
public class LiveConsoleHub : Hub
{
    private readonly LiveConsoleBroadcaster _broadcaster;

    public LiveConsoleHub(LiveConsoleBroadcaster broadcaster)
    {
        _broadcaster = broadcaster;
    }

    public override async Task OnConnectedAsync()
    {
        // Build identity first — the dock displays it before any events
        // even arrive, so a visitor can see the BFF's git sha + start time
        // immediately on connect.
        await Clients.Caller.SendAsync("OnConsoleHello", _broadcaster.Hello);

        var recent = _broadcaster.GetRecent();
        if (recent.Count > 0)
        {
            await Clients.Caller.SendAsync("OnConsoleBackfill", recent);
        }
        await base.OnConnectedAsync();
    }
}
