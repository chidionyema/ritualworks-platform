namespace Haworks.BffWeb.Api.Demo;

/// <summary>
/// Build identity stamped into the live-console dock the moment a client
/// connects. Lets the visitor see at a glance whether the BFF process they
/// are looking at is the one they expect — short git SHA, instance id, and
/// process startup time. The corresponding frontend identity is injected
/// at Vite-config time. Together they catch "stale state" issues
/// (long-running dev servers, forgotten rebuilds) without needing browser
/// or cluster diagnostics.
/// </summary>
public sealed record LiveConsoleHello(
    string Service,
    string InstanceId,
    string GitSha,
    string ProcessStartedAt);
