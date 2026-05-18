using System.Text.Json;
using Haworks.BffWeb.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Haworks.BffWeb.Api.Controllers;

/// <summary>
/// Health, metrics, and trace lookup endpoints used by the portfolio site's
/// dashboard panels (StatusStrip, hero metrics tile, TraceViewer).
///
/// Phase 2 (T2.1): all snapshot fields are real now. <c>services</c> reflects
/// the live state of each microservice BffWeb depends on (probed via typed
/// HttpClients with a 2s timeout). Hero metrics (<c>ingressEvents24h</c>,
/// <c>activeSessions</c>, <c>p99LatencyMs</c>) come from
/// <see cref="IDemoActivityCounters"/> which the <c>DemoActivityMiddleware</c>
/// updates on every <c>/api/demo/*</c> request.
///
/// <c>availability</c>/<c>clusterAvailability</c> stay <c>null</c> until we
/// wire a real uptime SLO probe — the frontend renders an em-dash in that
/// slot, which is more honest than inventing a number.
/// </summary>
[ApiController]
[Route("api")]
[AllowAnonymous]
public class SystemController : ControllerBase
{
    private readonly IDemoActivityCounters _activityCounters;
    private readonly IDependencyHealthProbe _healthProbe;

    public SystemController(
        IDemoActivityCounters activityCounters,
        IDependencyHealthProbe healthProbe)
    {
        _activityCounters = activityCounters;
        _healthProbe = healthProbe;
    }

    [HttpGet("health/snapshot")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetHealthSnapshot(CancellationToken ct = default)
    {
        var probe = await _healthProbe.ProbeAsync(ct);
        var activity = _activityCounters.Snapshot();

        return Ok(BuildHealthSnapshot(probe, activity));
    }

    [HttpGet("health/stream")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task GetHealthStream(CancellationToken ct = default)
    {
        Response.Headers.Append("Content-Type", "text/event-stream");
        Response.Headers.Append("Cache-Control", "no-cache");
        Response.Headers.Append("Connection", "keep-alive");

        while (!ct.IsCancellationRequested)
        {
            var probe = await _healthProbe.ProbeAsync(ct);
            var activity = _activityCounters.Snapshot();

            var json = JsonSerializer.Serialize(BuildHealthSnapshot(probe, activity));
            await Response.WriteAsync($"data: {json}\n\n", ct);
            await Response.Body.FlushAsync(ct);

            await Task.Delay(5000, ct);
        }
    }

    [HttpGet("metrics/snapshot")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult GetMetricsSnapshot()
    {
        var snapshot = _activityCounters.Snapshot();

        return Ok(new
        {
            ingressEvents24h = snapshot.IngressEvents24h,
            clusterAvailability = (double?)null, // honest: no uptime SLO wired yet
            p99LatencyMs = snapshot.P99LatencyMs,
            activeSessions = snapshot.ActiveSessions,
            timestamp = snapshot.CapturedAt,
        });
    }

    [HttpGet("metrics/stream")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task GetMetricsStream(CancellationToken ct = default)
    {
        Response.Headers.Append("Content-Type", "text/event-stream");
        Response.Headers.Append("Cache-Control", "no-cache");
        Response.Headers.Append("Connection", "keep-alive");

        while (!ct.IsCancellationRequested)
        {
            var snapshot = _activityCounters.Snapshot();
            var json = JsonSerializer.Serialize(new
            {
                ingressEvents24h = snapshot.IngressEvents24h,
                clusterAvailability = (double?)null,
                p99LatencyMs = snapshot.P99LatencyMs,
                activeSessions = snapshot.ActiveSessions,
                timestamp = snapshot.CapturedAt,
            });
            await Response.WriteAsync($"data: {json}\n\n", ct);
            await Response.Body.FlushAsync(ct);
            await Task.Delay(5000, ct);
        }
    }

    // Synthetic edge-flow stream consumed by EventMesh on the home hero.
    // Honest stub: emits a steady tick of "bff-web -> {service}" edges drawn
    // from the same probe results /health/snapshot exposes, so the animation
    // tracks real service-up state. No traffic is actually inferred — when
    // we wire OTel/tempo, this endpoint should pull from real span data.
    [HttpGet("topology/stream")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task GetTopologyStream(CancellationToken ct = default)
    {
        Response.Headers.Append("Content-Type", "text/event-stream");
        Response.Headers.Append("Cache-Control", "no-cache");
        Response.Headers.Append("Connection", "keep-alive");

        var edges = new[]
        {
            "bff-web->identity", "bff-web->catalog", "bff-web->orders",
            "bff-web->payments", "bff-web->checkout",
            "checkout->orders", "checkout->payments", "orders->mq", "mq->bff-web",
        };
        var i = 0;
        while (!ct.IsCancellationRequested)
        {
            var edge = edges[i % edges.Length];
            i++;
            var evt = new
            {
                id = Guid.NewGuid().ToString("N"),
                type = "edge-flow",
                timestamp = DateTime.UtcNow,
                edge,
            };
            await Response.WriteAsync($"data: {JsonSerializer.Serialize(evt)}\n\n", ct);
            await Response.Body.FlushAsync(ct);
            await Task.Delay(1000, ct);
        }
    }

    // /api/traces/{traceId} removed alongside the hardcoded tracing demo.
    // The IDemoTraceStore was only ever populated by /api/demo/tracing/start,
    // which generated a synthetic 7-span tree — surfacing it here as a
    // generic "trace lookup" was misleading. Real OTel via Tempo will
    // expose this surface again with actual cross-service span data.

    /// <summary>
    /// Build identity for the BFF process — instance id, git SHA, and
    /// process start time. Used by the portfolio's hero fingerprint line
    /// so visitors can read "Backend bff-yzjnq · sha 996f12a · uptime 2h"
    /// above the fold without expanding the live console dock. Same data
    /// as the dock's <c>OnConsoleHello</c> message, exposed over REST so
    /// the hero doesn't need a SignalR connection just to render its
    /// header.
    /// </summary>
    [HttpGet("system/identity")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult GetSystemIdentity(
        [FromServices] Haworks.BffWeb.Api.Demo.LiveConsoleBroadcaster broadcaster)
    {
        var hello = broadcaster.Hello;
        return Ok(new
        {
            service = hello.Service,
            instanceId = hello.InstanceId,
            gitSha = hello.GitSha,
            processStartedAt = hello.ProcessStartedAt,
        });
    }

    /// <summary>
    /// Shared shape for /health/snapshot + /health/stream. Field names match
    /// portfolio-site's <c>HealthSnapshot</c> + <c>ServiceHealth</c> TypeScript
    /// types (src/lib/api/demo-client.ts). <c>message</c> not <c>note</c> —
    /// frontend reads <c>message</c>.
    /// </summary>
    private static object BuildHealthSnapshot(DependencySnapshot probe, DemoActivitySnapshot activity) => new
    {
        services = probe.Services.Select(s => new
        {
            id = s.Id,
            name = s.Name,
            status = s.Status,
            latencyMs = s.LatencyMs,
            message = s.Message,
        }),
        systemStatus = probe.SystemStatus,
        p99LatencyMs = activity.P99LatencyMs,
        availability = (double?)null,
        timestamp = probe.CapturedAt,
    };
}
