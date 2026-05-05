namespace Haworks.BffWeb.Application.Interfaces;

/// <summary>
/// Real-time push surface for the portfolio site's interactive demos.
/// Each Notify* method emits a SignalR message scoped to the demo session
/// (Guid.Empty broadcasts to all connected clients — used for global infra
/// events like vault rotation that aren't per-session).
///
/// Production impl is SignalRDemoHubNotifier (BffWeb.Api/SignalR/DemoHub.cs);
/// it must be Singleton-registered because IResilienceMetrics implementations
/// downstream consume it and ASP.NET Core's ValidateScopes catches the
/// captive-dependency at boot otherwise.
/// </summary>
public interface IDemoHubNotifier
{
    Task NotifySagaStepAsync(SagaStepEvent e, CancellationToken ct = default);
    Task NotifyCircuitBreakerStateAsync(CircuitBreakerStateEvent e, CancellationToken ct = default);
    Task NotifyCacheEventAsync(CacheEvent e, CancellationToken ct = default);
    Task NotifyEventFlowAsync(EventFlowEvent e, CancellationToken ct = default);
    Task NotifyVaultRotationAsync(VaultRotationEvent e, CancellationToken ct = default);
    Task NotifyRateLimitAsync(RateLimitEvent e, CancellationToken ct = default);
    Task NotifyConcurrencyEventAsync(ConcurrencyEvent e, CancellationToken ct = default);
}

// Wire-format records — must stay byte-compatible with the portfolio-site
// frontend's TypeScript types (src/lib/api/demo-client.ts + signalr.ts).
// Adding a property is safe; renaming or reordering breaks the client.
public sealed record SagaStepEvent(Guid SessionId, string Step, string Service, string Status, string Description, int ProgressPercent, DateTime Timestamp);
public sealed record CircuitBreakerStateEvent(Guid SessionId, string Service, string State, DateTime Timestamp);
public sealed record CacheEvent(Guid SessionId, string Action, string Key, string Result, int? LatencyMs, DateTime Timestamp);
public sealed record EventFlowEvent(Guid SessionId, string EventId, string Stage, string? Data, DateTime Timestamp);
public sealed record VaultRotationEvent(Guid SessionId, string Stage, int Version, string? PreviousVersion, DateTime Timestamp);
public sealed record RateLimitEvent(Guid SessionId, bool Allowed, int Remaining, int? RetryAfterSeconds, DateTime Timestamp);
public sealed record ConcurrencyEvent(Guid SessionId, string Action, string ResourceId, string Status, int Version, DateTime Timestamp);
