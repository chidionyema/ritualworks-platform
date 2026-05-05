using Haworks.BffWeb.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Haworks.BffWeb.Api.SignalR;

/// <summary>
/// SignalR hub for the portfolio site's interactive demos. Clients call
/// SubscribeToSession(sessionId) right after the negotiate, and the
/// SignalRDemoHubNotifier (below) emits events scoped to the matching
/// "demo-{sessionId}" group.
///
/// [AllowAnonymous] is intentional — the demo surface is public; rate
/// limiting + per-session scoping prevent abuse.
/// </summary>
[AllowAnonymous]
public class DemoHub : Hub
{
    private readonly ILogger<DemoHub> _logger;

    public DemoHub(ILogger<DemoHub> logger)
    {
        _logger = logger;
    }

    public async Task SubscribeToSession(string sessionId)
    {
        if (!Guid.TryParse(sessionId, out var parsedSessionId))
        {
            await Clients.Caller.SendAsync("OnSubscriptionError", "Invalid session ID format");
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, GetGroupName(parsedSessionId));

        _logger.LogDebug(
            "Client {ConnectionId} subscribed to demo session {SessionId}",
            Context.ConnectionId, sessionId);

        await Clients.Caller.SendAsync("OnSubscribed", sessionId);
    }

    public async Task UnsubscribeFromSession(string sessionId)
    {
        if (Guid.TryParse(sessionId, out var parsedSessionId))
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, GetGroupName(parsedSessionId));
        }
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogDebug(
            "Client {ConnectionId} disconnected from DemoHub. Reason: {Exception}",
            Context.ConnectionId, exception?.Message ?? "normal");

        await base.OnDisconnectedAsync(exception);
    }

    private static string GetGroupName(Guid sessionId) => $"demo-{sessionId}";
}

/// <summary>
/// SignalR-backed implementation of IDemoHubNotifier.
///
/// MUST be registered as Singleton: it only depends on IHubContext&lt;DemoHub&gt;
/// (Singleton-safe per SignalR's own DI) and ILogger. Registering Scoped
/// causes a captive-dependency runtime crash because downstream Singleton
/// services (like resilience metrics emitters) consume IDemoHubNotifier and
/// ASP.NET Core's ValidateScopes catches it in Development.
/// </summary>
public class SignalRDemoHubNotifier : IDemoHubNotifier
{
    private readonly IHubContext<DemoHub> _hubContext;
    private readonly ILogger<SignalRDemoHubNotifier> _logger;

    public SignalRDemoHubNotifier(
        IHubContext<DemoHub> hubContext,
        ILogger<SignalRDemoHubNotifier> logger)
    {
        _hubContext = hubContext;
        _logger = logger;
    }

    public Task NotifySagaStepAsync(SagaStepEvent e, CancellationToken ct = default) =>
        SendToSessionAsync(e.SessionId, "OnSagaStep", e, ct);

    public Task NotifyCircuitBreakerStateAsync(CircuitBreakerStateEvent e, CancellationToken ct = default) =>
        SendToSessionAsync(e.SessionId, "OnCircuitBreakerState", e, ct);

    public Task NotifyCacheEventAsync(CacheEvent e, CancellationToken ct = default) =>
        SendToSessionAsync(e.SessionId, "OnCacheEvent", e, ct);

    public Task NotifyEventFlowAsync(EventFlowEvent e, CancellationToken ct = default) =>
        SendToSessionAsync(e.SessionId, "OnEventFlow", e, ct);

    public Task NotifyVaultRotationAsync(VaultRotationEvent e, CancellationToken ct = default) =>
        SendToSessionAsync(e.SessionId, "OnVaultRotation", e, ct);

    public Task NotifyRateLimitAsync(RateLimitEvent e, CancellationToken ct = default) =>
        SendToSessionAsync(e.SessionId, "OnRateLimit", e, ct);

    public Task NotifyConcurrencyEventAsync(ConcurrencyEvent e, CancellationToken ct = default) =>
        SendToSessionAsync(e.SessionId, "OnConcurrency", e, ct);

    private async Task SendToSessionAsync(Guid sessionId, string methodName, object payload, CancellationToken ct)
    {
        if (sessionId == Guid.Empty)
        {
            // Global broadcast for cross-session infra events (e.g. vault rotation).
            await _hubContext.Clients.All.SendAsync(methodName, payload, ct);
            _logger.LogDebug("Globally broadcasted {Method} notification", methodName);
        }
        else
        {
            var groupName = GetGroupName(sessionId);
            await _hubContext.Clients.Group(groupName).SendAsync(methodName, payload, ct);
            _logger.LogDebug("Sent {Method} for demo session {SessionId}", methodName, sessionId);
        }
    }

    private static string GetGroupName(Guid sessionId) => $"demo-{sessionId}";
}
