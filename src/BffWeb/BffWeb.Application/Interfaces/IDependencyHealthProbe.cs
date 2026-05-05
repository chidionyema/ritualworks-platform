namespace Haworks.BffWeb.Application.Interfaces;

public sealed record DependencyStatus(
    string Id,
    string Name,
    string Status,        // "online" | "degraded" | "offline"
    long? LatencyMs,      // null only if probe was not attempted
    string? Message);     // optional one-line context (matches frontend type)

public sealed record DependencySnapshot(
    IReadOnlyList<DependencyStatus> Services,
    string SystemStatus,  // aggregate: "healthy" | "degraded" | "down"
    DateTime CapturedAt);

/// <summary>
/// Probes the live state of each microservice BffWeb depends on
/// (catalog-svc, orders-svc, payments-svc, identity-svc, checkout-svc) plus
/// the message broker (RabbitMQ via MassTransit's IBus). Each probe has a
/// short per-target timeout and runs in parallel; total wall-clock is bounded
/// by the slowest target rather than the sum.
///
/// What visitors see in the StatusStrip is the actual state of the seven-
/// service distributed system right now — not a hardcoded "all online" claim.
/// </summary>
public interface IDependencyHealthProbe
{
    Task<DependencySnapshot> ProbeAsync(CancellationToken ct = default);
}
