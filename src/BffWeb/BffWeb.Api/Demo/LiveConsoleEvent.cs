using Haworks.BffWeb.Api.Middleware;

namespace Haworks.BffWeb.Api.Demo;

/// <summary>
/// One row in the visitor-facing live console dock. Captured per
/// <c>/api/*</c> request handled by this BFF process and fanned out to every
/// browser connected to <c>/hubs/console</c>.
///
/// The event is intentionally minimal — enough for a visitor to recognise
/// "I just pressed that button and the BFF actually saw it" and to copy a
/// reproducer curl command. Trace correlation lives in the existing
/// <c>X-Correlation-ID</c> / OTel pipeline; this dock is the human-visible
/// surface.
///
/// <para><c>Upstreams</c> is the per-request list of upstream replicas
/// hit by the BFF while serving this request — captured from each
/// outbound call's <c>X-Instance-Id</c> response header. Empty for
/// requests that didn't fan out to any backend service. The dock renders
/// it as "bff-yumv → catalog-7e3f, orders-bb19" — the visible proof that
/// the BFF is actually load-balancing across replicas.</para>
/// </summary>
public sealed record LiveConsoleEvent(
    string Ts,
    string Service,
    string InstanceId,
    string Method,
    string Path,
    int Status,
    double DurationMs,
    string? TraceId,
    string? CorrelationId,
    IReadOnlyList<UpstreamHop> Upstreams);
